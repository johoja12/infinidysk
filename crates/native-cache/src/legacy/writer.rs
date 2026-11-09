//! Opt-in v1 publication protocol. Existing committed blocks and metadata formats stay intact.
use super::{Entry, Folder, Identity, Legacy, Manifest, Root, leaf};
use crate::{BLOCK, Error, hex};
use bytes::Bytes;
use nix::fcntl::{OFlag, openat};
use nix::sys::stat::{Mode, mkdirat};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use sqlx::{Row as _, SqlitePool};
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::{self, BufRead as _, Read as _, Write as _};
use std::os::unix::fs::{FileExt as _, MetadataExt as _};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError, Weak};
use tokio::sync::{Mutex as AsyncMutex, OwnedSemaphorePermit, Semaphore};
use utoipa::ToSchema;
use uuid::Uuid;

const OVERHEAD: u64 = 65_536;
mod eviction;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheEvictionPolicy)]
pub struct EvictionPolicy {
    pub max_age_days: u32,
    pub high_percent: u8,
    pub low_percent: u8,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheWriteFolder)]
pub struct WriteFolder {
    pub id: String,
    pub priority: i32,
    pub quota_bytes: u64,
    pub min_free_bytes: u64,
    /// Explicit permission for policy eviction. Omission preserves all payloads.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub eviction: Option<EvictionPolicy>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheWriteSettings)]
pub struct WriteSettings {
    pub folders: Vec<WriteFolder>,
    /// Zero selects a sparse file; other values select bounded chunk files.
    pub chunk_mb: u32,
}
impl WriteSettings {
    pub(super) fn validate(&self, roots: &[Folder]) -> Result<(), Error> {
        let mut ids = HashSet::new();
        if self.folders.is_empty()
            || self.folders.len() > 64
            || (self.chunk_mb != 0 && !(4..=256).contains(&self.chunk_mb))
        {
            return Err(Error::Invalid(
                "compatible writes need 1-64 configured folders and a valid chunk size",
            ));
        }
        for f in &self.folders {
            if !ids.insert(&f.id)
                || !roots.iter().any(|r| r.id == f.id)
                || f.quota_bytes < BLOCK
                || f.quota_bytes > i64::MAX.cast_unsigned()
                || f.eviction.as_ref().is_some_and(|p| {
                    p.low_percent == 0 || p.low_percent >= p.high_percent || p.high_percent > 100
                })
            {
                return Err(Error::Invalid(
                    "compatible write folders require unique registered identities and bounded quotas",
                ));
            }
        }
        Ok(())
    }
}

pub(super) async fn validate_schema(db: &SqlitePool) -> Result<(), Error> {
    for (table, columns) in [
        (
            "Entries",
            "Key Folder Length Bytes Access Pinned Dirty ItemId VerifiedBytes PendingBytes ChunkSize Generation DisplayName AccessCount",
        ),
        ("Blocks", "Key Offset Count Hash"),
        ("FolderTotals", "Folder Bytes"),
        (
            "RetiredEntries",
            "Key Folder Bytes ItemId DisplayName Length VerifiedBytes AccessCount LastAccessUnix",
        ),
    ] {
        let rows = sqlx::query("SELECT name FROM pragma_table_info($1)")
            .bind(table)
            .fetch_all(db)
            .await?;
        let present: HashSet<String> = rows
            .iter()
            .map(|r| r.try_get("name"))
            .collect::<Result<_, _>>()?;
        if columns.split_whitespace().any(|c| !present.contains(c)) {
            return Err(Error::Invalid(
                "compatible writes require the current v1 cache schema; no schema is upgraded automatically",
            ));
        }
    }
    let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('EntryAdded','EntryChanged','EntryRemoved','RetiredAdded','RetiredRemoved','BlockAdded','BlockRemoved')").fetch_one(db).await?;
    if count != 7 {
        return Err(Error::Invalid(
            "compatible writes require the v1 accounting triggers",
        ));
    }
    let invalid:bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM Entries WHERE Bytes<0 OR PendingBytes<0 OR PendingBytes>Bytes OR VerifiedBytes<0 OR VerifiedBytes>Length)").fetch_one(db).await?;
    if invalid {
        return Err(Error::Invalid(
            "compatible cache accounting needs reconciliation before writes",
        ));
    }
    Ok(())
}

#[derive(Default)]
struct State {
    active: HashMap<String, usize>,
    uncertain: HashSet<String>,
    reserved: HashMap<u64, u64>,
    stopped: bool,
}
pub struct Writer {
    legacy: Arc<Legacy>,
    settings: WriteSettings,
    gate: AsyncMutex<()>,
    locks: Mutex<HashMap<String, Weak<AsyncMutex<()>>>>,
    state: Mutex<State>,
    written: AtomicU64,
    probes: HashMap<String, Arc<Semaphore>>,
    eviction: Arc<AsyncMutex<eviction::State>>,
    #[cfg(test)]
    fault: Option<Fault>,
}
impl Writer {
    pub fn settings(&self) -> &WriteSettings {
        &self.settings
    }

    pub fn new(legacy: Arc<Legacy>, mut settings: WriteSettings) -> Result<Arc<Self>, Error> {
        settings.validate(
            &legacy
                .roots
                .iter()
                .map(|(id, root)| Folder {
                    owner_lock_path: None,
                    id: id.clone(),
                    path: root.path.clone(),
                })
                .collect::<Vec<_>>(),
        )?;
        settings
            .folders
            .sort_by_key(|f| std::cmp::Reverse(f.priority));
        let probes = settings
            .folders
            .iter()
            .map(|f| (f.id.clone(), Arc::new(Semaphore::new(1))))
            .collect();
        Ok(Arc::new(Self {
            legacy,
            settings,
            gate: AsyncMutex::new(()),
            locks: Mutex::default(),
            state: Mutex::default(),
            written: AtomicU64::new(0),
            probes,
            eviction: Arc::default(),
            #[cfg(test)]
            fault: None,
        }))
    }
    pub fn written_bytes(&self) -> u64 {
        self.written.load(Ordering::Relaxed)
    }
    fn device_reserve(&self, device: u64) -> u64 {
        self.settings
            .folders
            .iter()
            .filter(|f| {
                self.legacy
                    .roots
                    .get(&f.id)
                    .is_some_and(|r| r.device == device)
            })
            .map(|f| f.min_free_bytes)
            .max()
            .unwrap_or(0)
    }
    fn lock(&self, key: String) -> Arc<AsyncMutex<()>> {
        let mut locks = self.locks.lock().unwrap_or_else(PoisonError::into_inner);
        if locks.len() >= 4096 {
            locks.retain(|_, v| v.strong_count() > 0);
        }
        if let Some(lock) = locks.get(&key).and_then(Weak::upgrade) {
            return lock;
        }
        let lock = Arc::new(AsyncMutex::new(()));
        locks.insert(key, Arc::downgrade(&lock));
        lock
    }
    /// The owned task completes its protocol even after the request deadline expires.
    /// Its permit, source ownership and per-block lock cannot be released by caller cancellation.
    pub async fn put(
        self: &Arc<Self>,
        item: Uuid,
        generation: String,
        size: u64,
        block: u64,
        data: Bytes,
        permit: OwnedSemaphorePermit,
    ) -> Result<bool, Error> {
        let this = self.clone();
        tokio::spawn(async move {
            let _permit = permit;
            this.publish(item, generation, size, block, data).await
        })
        .await
        .map_err(|_| Error::Invalid("compatible cache publication task failed"))?
    }
    async fn publish(
        self: &Arc<Self>,
        item: Uuid,
        generation: String,
        size: u64,
        block: u64,
        data: Bytes,
    ) -> Result<bool, Error> {
        let Some(start) = block
            .checked_mul(BLOCK)
            .filter(|s| *s < size && size <= i64::MAX.cast_unsigned())
        else {
            return Ok(false);
        };
        if data.len() as u64 != BLOCK.min(size - start) || generation.len() > 4096 {
            return Ok(false);
        }
        let id = item.simple().to_string();
        let encoded = format!("{}:{id}{}:{generation}:{size}", id.len(), generation.len());
        let key = hex(&Sha256::digest(encoded.as_bytes()));
        let _entry_guard = self.legacy.entry_lock(&key).read_owned().await;
        let block_guard = self.legacy.block_lock(&key, block).write_owned().await;
        let placement = self.lock(key.clone()).lock_owned().await;
        let hash = Sha256::digest(&data).to_vec();
        let existing = self.legacy.find(item, &generation, size).await?;
        if let Some(entry) = &existing {
            if let Some(record) = self
                .legacy
                .blocks(
                    entry,
                    i64::try_from(start).map_err(|_| Error::Invalid("invalid offset"))? - 1,
                    1,
                )
                .await?
                .into_iter()
                .find(|b| b.offset == start.cast_signed())
            {
                if record.hash != hash {
                    return Err(Error::Invalid(
                        "an existing committed block cannot be replaced",
                    ));
                }
                let _reading = block_guard.downgrade();
                let root = self
                    .legacy
                    .roots
                    .get(&entry.folder)
                    .cloned()
                    .ok_or(Error::Invalid("cache folder is unavailable"))?;
                let entry = entry.clone();
                tokio::task::spawn_blocking(move || verify_existing(&root, &entry, start, &data))
                    .await
                    .map_err(|_| Error::Invalid("cache verification task failed"))??;
                return Ok(true);
            }
            let pending: i64 = sqlx::query_scalar("SELECT PendingBytes FROM Entries WHERE Key=$1")
                .bind(&key)
                .fetch_one(&self.legacy.db)
                .await?;
            let uncertain = {
                let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
                if pending == 0 {
                    state.uncertain.remove(&key);
                }
                state.uncertain.contains(&key) || (pending > 0 && !state.active.contains_key(&key))
            };
            if uncertain {
                let Some(root) = self
                    .legacy
                    .roots
                    .get(&entry.folder)
                    .filter(|_| self.settings.folders.iter().any(|f| f.id == entry.folder))
                    .cloned()
                else {
                    return Ok(false);
                };
                drop(placement);
                return self
                    .reconcile(
                        entry,
                        root,
                        start,
                        &hash,
                        (data.len() as u64)
                            .next_multiple_of(65_536)
                            .saturating_add(4096),
                    )
                    .await;
            }
        }
        let mut free = HashMap::new();
        for folder in &self.settings.folders {
            let Some(root) = self.legacy.roots.get(&folder.id).cloned() else {
                continue;
            };
            let Some(probe) = self.probes.get(&folder.id) else {
                continue;
            };
            let Ok(permit) = probe.clone().try_acquire_owned() else {
                continue;
            };
            let job = tokio::task::spawn_blocking(move || {
                let _permit = permit;
                root.validate()?;
                let stat = nix::sys::statvfs::fstatvfs(&root.file)?;
                if stat.flags().contains(nix::sys::statvfs::FsFlags::ST_RDONLY) {
                    return Err(Error::Invalid(
                        "compatible write folder is mounted read-only",
                    ));
                }
                Ok::<_, Error>(crate::filesystem::bytes(
                    stat.blocks_available(),
                    stat.fragment_size(),
                ))
            });
            if let Ok(Ok(Ok(bytes))) =
                tokio::time::timeout(std::time::Duration::from_secs(1), job).await
            {
                free.insert(folder.id.clone(), bytes);
            }
        }
        let gate = self.gate.lock().await;
        let candidate = existing.unwrap_or_else(|| self.new_entry(&key, &id, &generation, size));
        let (entry, root, allocation) = self.place(candidate, data.len() as u64, &free).await?;
        let Some(root) = root else {
            return Ok(false);
        };
        if self
            .state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .stopped
        {
            return Ok(false);
        }
        let reservation = Reservation::new(
            self.clone(),
            key.clone(),
            root.device,
            allocation.saturating_add(OVERHEAD),
        );
        sqlx::query(
            "UPDATE Entries SET Dirty=1,Bytes=Bytes+$2,PendingBytes=PendingBytes+$2,Access=$3 WHERE Key=$1",
        )
        .bind(&key)
        .bind(allocation.cast_signed()).bind(crate::now()/300_000)
        .execute(&self.legacy.db)
        .await?;
        drop(gate);
        self.point("reserved")?;
        // Publish the complete manifest under placement before parallel blocks can enter.
        // No catalogue-wide gate is held over this storage I/O.
        let create = entry.clone();
        let volume = root.clone();
        let prepare = self.clone();
        tokio::task::spawn_blocking(move || create_entry(&prepare, &volume, &create))
            .await
            .map_err(|_| Error::Invalid("cache entry preparation failed"))??;
        drop(placement);
        let writer = self.clone();
        let record = entry.clone();
        let digest = hash.clone();
        let physical = tokio::task::spawn_blocking(move || {
            writer.physical(&root, &record, start, &data, &digest)
        })
        .await
        .map_err(|_| Error::Invalid("compatible cache I/O task failed"))??;
        self.point("journal_synced")?;
        self.commit(&entry, start, &hash, allocation, physical)
            .await?;
        self.point("catalogue_committed")?;
        reservation.finish();
        self.written
            .fetch_add(BLOCK.min(size - start), Ordering::Relaxed);
        drop(block_guard);
        Ok(true)
    }

    fn new_entry(&self, key: &str, id: &str, generation: &str, size: u64) -> Entry {
        Entry {
            key: key.into(),
            folder: String::new(),
            item: id.into(),
            size: size.cast_signed(),
            pinned: 0,
            generation: Some(generation.into()),
            chunk: if self.settings.chunk_mb == 0 {
                0
            } else {
                (u64::from(self.settings.chunk_mb) * 1_000_000)
                    .next_multiple_of(BLOCK)
                    .cast_signed()
            },
        }
    }
    async fn place(
        &self,
        mut entry: Entry,
        length: u64,
        free: &HashMap<String, u64>,
    ) -> Result<(Entry, Option<Arc<Root>>, u64), Error> {
        let allocation = length.next_multiple_of(65_536).saturating_add(4096);
        for option in &self.settings.folders {
            if !entry.folder.is_empty() && entry.folder != option.id {
                continue;
            }
            let retired: bool = sqlx::query_scalar(
                "SELECT EXISTS(SELECT 1 FROM RetiredEntries WHERE Key=$1 AND Folder=$2)",
            )
            .bind(&entry.key)
            .bind(&option.id)
            .fetch_one(&self.legacy.db)
            .await?;
            if retired {
                continue;
            }
            let Some(root) = self.legacy.roots.get(&option.id).cloned() else {
                continue;
            };
            let Some(free) = free.get(&option.id).copied() else {
                continue;
            };
            let reserved = self
                .state
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .reserved
                .get(&root.device)
                .copied()
                .unwrap_or(0);
            let used: i64 = sqlx::query_scalar(
                "SELECT COALESCE((SELECT Bytes FROM FolderTotals WHERE Folder=$1),0)",
            )
            .bind(&option.id)
            .fetch_one(&self.legacy.db)
            .await?;
            let overhead = if entry.folder.is_empty() { OVERHEAD } else { 0 };
            if used < 0
                || used
                    .cast_unsigned()
                    .saturating_add(allocation)
                    .saturating_add(overhead)
                    > option.quota_bytes
                || free
                    < self
                        .device_reserve(root.device)
                        .saturating_add(reserved)
                        .saturating_add(allocation)
                        .saturating_add(overhead)
            {
                continue;
            }
            if entry.folder.is_empty() {
                entry.folder.clone_from(&option.id);
                sqlx::query("INSERT INTO Entries(Key,Folder,Length,Bytes,Access,Dirty,ItemId,Generation,DisplayName,ChunkSize) VALUES($1,$2,$3,$4,$5,1,$6,$7,'',$8)")
                    .bind(&entry.key).bind(&entry.folder).bind(entry.size).bind(OVERHEAD.cast_signed()).bind(crate::now()/300_000).bind(&entry.item).bind(&entry.generation).bind(entry.chunk).execute(&self.legacy.db).await?;
            }
            return Ok((entry, Some(root), allocation));
        }
        Ok((entry, None, allocation))
    }
    fn physical(
        &self,
        root: &Root,
        entry: &Entry,
        start: u64,
        data: &[u8],
        hash: &[u8],
    ) -> Result<u64, Error> {
        let dir = root.entry(&entry.key)?;
        verify_manifest(root, &dir, entry)?;
        let name = payload_name(entry, start);
        let (file, created) = writable(&dir, &name, root.device)?;
        self.point("before_write")?;
        #[cfg(test)]
        if matches!(self.fault, Some(Fault::Short("payload"))) {
            file.write_all_at(
                &data[..data.len() / 2],
                start
                    .checked_rem(entry.chunk.cast_unsigned())
                    .unwrap_or(start),
            )?;
            return Err(io::Error::new(io::ErrorKind::WriteZero, "injected short write").into());
        }
        file.write_all_at(
            data,
            start
                .checked_rem(entry.chunk.cast_unsigned())
                .unwrap_or(start),
        )?;
        self.point("written")?;
        self.point("before_payload_sync")?;
        file.sync_all()?;
        if created {
            dir.sync_all()?;
        }
        self.point("payload_synced")?;
        let journal_lock = self.lock(format!("{}:journal", entry.key));
        let _journal = journal_lock.blocking_lock();
        let (journal, _) = writable(&dir, "ranges.journal", root.device)?;
        let length = journal.metadata()?.len();
        if length
            > (entry.size.cast_unsigned() / BLOCK + 1)
                .saturating_mul(256)
                .saturating_add(OVERHEAD)
                .min(128 * 1024 * 1024)
        {
            return Err(Error::Invalid(
                "legacy journal needs explicit reconciliation",
            ));
        }
        if length != 0 {
            let mut last = [0];
            leaf(&dir, "ranges.journal", root.device)?.read_exact_at(&mut last, length - 1)?;
            if last != *b"\n" {
                return Err(Error::Invalid(
                    "legacy journal has an incomplete tail; publication held",
                ));
            }
        }
        let line = serde_json::to_vec(&Journal {
            offset: start.cast_signed(),
            count: i32::try_from(data.len()).map_err(|_| Error::Invalid("invalid block length"))?,
            hash: hex(hash).to_ascii_uppercase(),
        })
        .map_err(|_| Error::Invalid("cannot encode cache journal record"))?;
        #[cfg(test)]
        if matches!(self.fault, Some(Fault::Short("journal"))) {
            journal.write_all_at(&line[..line.len() / 2], length)?;
            return Err(
                io::Error::new(io::ErrorKind::WriteZero, "injected short journal write").into(),
            );
        }
        journal.write_all_at(&line, length)?;
        journal.write_all_at(b"\n", length + line.len() as u64)?;
        self.point("journal_written")?;
        self.point("before_journal_sync")?;
        journal.sync_all()?;
        // Another failed writer may have created either leaf without durably
        // publishing its name. Flush the directory even when both leaves exist.
        self.point("before_entry_sync")?;
        dir.sync_all()?;
        root.validate()?;
        Ok(OVERHEAD
            .saturating_add(file.metadata()?.blocks().saturating_mul(512))
            .saturating_add(journal.metadata()?.blocks().saturating_mul(512)))
    }
    async fn commit(
        &self,
        entry: &Entry,
        start: u64,
        hash: &[u8],
        allocation: u64,
        physical: u64,
    ) -> Result<(), Error> {
        self.point("before_catalogue")?;
        let mut tx = self.legacy.db.begin().await?;
        let inserted =
            sqlx::query("INSERT OR IGNORE INTO Blocks(Key,Offset,Count,Hash) VALUES($1,$2,$3,$4)")
                .bind(&entry.key)
                .bind(start.cast_signed())
                .bind(BLOCK.min(entry.size.cast_unsigned() - start).cast_signed())
                .bind(hash)
                .execute(&mut *tx)
                .await?
                .rows_affected();
        if inserted != 0 {
            sqlx::query("UPDATE Entries SET PendingBytes=MAX(0,PendingBytes-$2),Dirty=CASE WHEN PendingBytes<=$2 THEN 0 ELSE 1 END,Bytes=MAX(Bytes,$3) WHERE Key=$1")
                .bind(&entry.key).bind(allocation.cast_signed()).bind(physical.min(i64::MAX.cast_unsigned()).cast_signed()).execute(&mut *tx).await?;
        } else {
            let (count, observed): (i64, Vec<u8>) =
                sqlx::query_as("SELECT Count,Hash FROM Blocks WHERE Key=$1 AND Offset=$2")
                    .bind(&entry.key)
                    .bind(start.cast_signed())
                    .fetch_one(&mut *tx)
                    .await?;
            if count != BLOCK.min(entry.size.cast_unsigned() - start).cast_signed()
                || observed != hash
            {
                return Err(Error::Invalid(
                    "published cache block differs from the requested identity",
                ));
            }
        }
        tx.commit().await?;
        self.legacy
            .invalid
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .remove(&(entry.key.clone(), start));
        Ok(())
    }
    async fn reconcile(
        self: &Arc<Self>,
        entry: &Entry,
        root: Arc<Root>,
        start: u64,
        hash: &[u8],
        allocation: u64,
    ) -> Result<bool, Error> {
        let entry_copy = entry.clone();
        let expected = hash.to_vec();
        let journal_lock = self.clone_lock(&entry.key);
        let writer = self.clone();
        let proof = tokio::task::spawn_blocking(move || {
            let _journal = journal_lock.blocking_lock();
            let dir = root.entry(&entry_copy.key)?;
            verify_manifest(&root, &dir, &entry_copy)?;
            create_entry(&writer, &root, &entry_copy)?;
            let journal = leaf(&dir, "ranges.journal", root.device)?;
            if journal.metadata()?.len() > 128 * 1024 * 1024 {
                return Ok::<_, Error>(None);
            }
            let mut input = io::BufReader::new(journal);
            let mut matched = false;
            loop {
                let mut line = String::new();
                let n = (&mut input).take(513).read_line(&mut line)?;
                if n == 0 {
                    break;
                }
                if !line.ends_with('\n') || n > 512 {
                    break;
                }
                let record: Journal = serde_json::from_str(&line)
                    .map_err(|_| Error::Invalid("invalid committed journal record"))?;
                if record.offset == start.cast_signed()
                    && u64::try_from(record.count)
                        == Ok(BLOCK.min(entry_copy.size.cast_unsigned() - start))
                    && record.hash.eq_ignore_ascii_case(&hex(&expected))
                {
                    matched = true;
                }
            }
            if !matched {
                return Ok(None);
            }
            let file = leaf(&dir, &payload_name(&entry_copy, start), root.device)?;
            let mut bytes = vec![
                0;
                usize::try_from(BLOCK.min(entry_copy.size.cast_unsigned() - start))
                    .map_err(|_| Error::Invalid("invalid block length"))?
            ];
            file.read_exact_at(
                &mut bytes,
                start
                    .checked_rem(entry_copy.chunk.cast_unsigned())
                    .unwrap_or(start),
            )?;
            if Sha256::digest(&bytes).as_slice() != expected {
                return Err(Error::Invalid("pending cache block checksum differs"));
            }
            // Readback is a fresh durability proof even if the old process died before fsync.
            file.sync_all()?;
            input.get_ref().sync_all()?;
            dir.sync_all()?;
            root.validate()?;
            Ok(Some(OVERHEAD.saturating_add(
                file.metadata()?.blocks().saturating_mul(512),
            )))
        })
        .await
        .map_err(|_| Error::Invalid("cache reconciliation failed"))??;
        let Some(physical) = proof else {
            return Ok(false);
        };
        self.commit(entry, start, hash, allocation, physical)
            .await?;
        let pending: i64 = sqlx::query_scalar("SELECT PendingBytes FROM Entries WHERE Key=$1")
            .bind(&entry.key)
            .fetch_one(&self.legacy.db)
            .await?;
        if pending == 0 {
            self.state
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .uncertain
                .remove(&entry.key);
        }
        Ok(true)
    }
    fn clone_lock(&self, key: &str) -> Arc<AsyncMutex<()>> {
        self.lock(format!("{key}:journal"))
    }
    #[allow(clippy::unused_self, clippy::unnecessary_wraps)] // The same protocol hooks inject failures only in test builds.
    fn point(&self, stage_name: &'static str) -> Result<(), Error> {
        #[cfg(not(test))]
        let _ = stage_name;
        #[cfg(test)]
        match &self.fault {
            Some(Fault::Error(stage, code)) if *stage == stage_name => {
                return Err(io::Error::from_raw_os_error(*code).into());
            }
            Some(Fault::Crash(stage)) if *stage == stage_name => std::process::abort(),
            Some(Fault::Gate(stage, entered, gate)) if *stage == stage_name => {
                entered.store(true, Ordering::Release);
                let (lock, wake) = &**gate;
                let mut released = lock.lock().unwrap_or_else(PoisonError::into_inner);
                while !*released {
                    released = wake.wait(released).unwrap_or_else(PoisonError::into_inner);
                }
                drop(released);
            }
            _ => {}
        }
        Ok(())
    }
}
#[cfg(test)]
enum Fault {
    Error(&'static str, i32),
    Crash(&'static str),
    Short(&'static str),
    Gate(
        &'static str,
        Arc<std::sync::atomic::AtomicBool>,
        Arc<(Mutex<bool>, std::sync::Condvar)>,
    ),
}
#[cfg(test)]
mod tests;
struct Reservation {
    writer: Arc<Writer>,
    key: String,
    device: u64,
    bytes: u64,
    complete: bool,
}
impl Reservation {
    fn new(writer: Arc<Writer>, key: String, device: u64, bytes: u64) -> Self {
        {
            let mut state = writer.state.lock().unwrap_or_else(PoisonError::into_inner);
            *state.active.entry(key.clone()).or_default() += 1;
            *state.reserved.entry(device).or_default() += bytes;
        }
        Self {
            writer,
            key,
            device,
            bytes,
            complete: false,
        }
    }
    fn finish(mut self) {
        self.complete = true;
    }
}
impl Drop for Reservation {
    fn drop(&mut self) {
        let mut state = self
            .writer
            .state
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if let Some(count) = state.active.get_mut(&self.key) {
            *count -= 1;
            if *count == 0 {
                state.active.remove(&self.key);
            }
        }
        if let Some(bytes) = state.reserved.get_mut(&self.device) {
            *bytes = bytes.saturating_sub(self.bytes);
            if *bytes == 0 {
                state.reserved.remove(&self.device);
            }
        }
        if !self.complete {
            if state.uncertain.len() < 4096 {
                state.uncertain.insert(self.key.clone());
            } else {
                state.stopped = true;
            }
        }
    }
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Journal {
    offset: i64,
    count: i32,
    hash: String,
}
fn directory(writer: &Writer, parent: &File, name: &str, device: u64) -> Result<File, Error> {
    match mkdirat(parent, name, Mode::S_IRWXU) {
        Ok(()) | Err(nix::errno::Errno::EEXIST) => {}
        Err(e) => return Err(e.into()),
    }
    let dir = File::from(openat(
        parent,
        name,
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    )?);
    if dir.metadata()?.dev() != device {
        return Err(Error::Invalid("cache entry crossed devices"));
    }
    // An earlier mkdir can be visible after a failed parent flush. EEXIST
    // proves visibility, not durability, including for another entry's shard.
    writer.point("before_directory_sync")?;
    parent.sync_all()?;
    Ok(dir)
}
fn create_entry(writer: &Writer, root: &Root, entry: &Entry) -> Result<(), Error> {
    root.validate()?;
    let version = directory(writer, &root.file, "v1", root.device)?;
    let shard = directory(writer, &version, &entry.key[..2], root.device)?;
    let dir = directory(writer, &shard, &entry.key, root.device)?;
    match openat(
        &dir,
        "manifest.json",
        OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW,
        Mode::S_IRUSR | Mode::S_IWUSR,
    ) {
        Ok(fd) => {
            let mut file = File::from(fd);
            let manifest = Manifest {
                version: if entry.chunk == 0 { 1 } else { 2 },
                identity: Identity {
                    item_id: entry.item.clone(),
                    generation: entry
                        .generation
                        .clone()
                        .ok_or(Error::Invalid("missing source generation"))?,
                    length: entry.size,
                },
                chunk_size: entry.chunk,
            };
            serde_json::to_writer(&mut file, &manifest)
                .map_err(|_| Error::Invalid("cannot encode manifest"))?;
            file.flush()?;
            file.sync_all()?;
            dir.sync_all()?;
        }
        Err(nix::errno::Errno::EEXIST) => verify_manifest(root, &dir, entry)?,
        Err(e) => return Err(e.into()),
    }
    root.validate()?;
    Ok(())
}
fn verify_manifest(root: &Root, dir: &File, entry: &Entry) -> Result<(), Error> {
    let file = leaf(dir, "manifest.json", root.device)?;
    if file.metadata()?.len() > 65_536 {
        return Err(Error::Invalid("oversized cache manifest"));
    }
    let manifest: Manifest =
        serde_json::from_reader(file).map_err(|_| Error::Invalid("invalid cache manifest"))?;
    if manifest.identity.item_id != entry.item
        || Some(manifest.identity.generation) != entry.generation
        || manifest.identity.length != entry.size
        || manifest.chunk_size != entry.chunk
        || manifest.version != if entry.chunk == 0 { 1 } else { 2 }
    {
        return Err(Error::Invalid("cache manifest differs from source"));
    }
    Ok(())
}
fn writable(dir: &File, name: &str, device: u64) -> Result<(File, bool), Error> {
    let (file, created) = match openat(
        dir,
        name,
        OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::S_IRUSR | Mode::S_IWUSR,
    ) {
        Ok(fd) => (File::from(fd), true),
        Err(nix::errno::Errno::EEXIST) => (
            File::from(openat(
                dir,
                name,
                OFlag::O_WRONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
                Mode::empty(),
            )?),
            false,
        ),
        Err(e) => return Err(e.into()),
    };
    let stat = file.metadata()?;
    if !stat.is_file() || stat.nlink() != 1 || stat.dev() != device {
        return Err(Error::Invalid("unsafe cache payload or journal"));
    }
    Ok((file, created))
}
fn verify_existing(root: &Root, entry: &Entry, start: u64, expected: &[u8]) -> Result<(), Error> {
    let dir = root.entry(&entry.key)?;
    verify_manifest(root, &dir, entry)?;
    let file = leaf(&dir, &payload_name(entry, start), root.device)?;
    let offset = start
        .checked_rem(entry.chunk.cast_unsigned())
        .unwrap_or(start);
    let mut scratch = vec![0; 65_536];
    for (index, chunk) in expected.chunks(65_536).enumerate() {
        file.read_exact_at(&mut scratch[..chunk.len()], offset + index as u64 * 65_536)?;
        if &scratch[..chunk.len()] != chunk {
            return Err(Error::Invalid(
                "existing cache payload differs; it was not overwritten",
            ));
        }
    }
    root.validate()?;
    Ok(())
}
fn payload_name(entry: &Entry, start: u64) -> String {
    start.checked_div(entry.chunk.cast_unsigned()).map_or_else(
        || "content.data".into(),
        |part| format!("chunk-{part:016x}.data"),
    )
}
