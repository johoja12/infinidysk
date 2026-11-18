//! Recover durable publication records without fetching or rereading source media.
use super::{Entry, Error, Journal, Legacy, Root, Uuid, Writer, leaf, verify_manifest};
use crate::{BLOCK, hex};
use sha2::{Digest as _, Sha256};
use std::collections::HashMap;
use std::io::{self, BufRead as _, Read as _};
use std::sync::{Arc, PoisonError};
use std::time::{Duration, Instant};

const JOURNAL_LIMIT: u64 = 128 * 1024 * 1024;
const RECORDS_PER_PASS: usize = 65_536;
const BLOCKS_PER_PASS: usize = 4;
const ENTRIES_PER_PASS: usize = 4;

#[derive(Default)]
pub(super) struct State {
    after: String,
    entries: HashMap<String, Progress>,
}
#[derive(Default)]
struct Progress {
    snapshot: Option<[u8; 32]>,
    pending: i64,
    scanned: usize,
    allocation: u64,
    candidates: Vec<Journal>,
    warning: Option<String>,
}
impl Progress {
    fn restart(&mut self) {
        self.scanned = 0;
        self.allocation = 0;
        self.candidates.clear();
    }
}

impl Writer {
    pub(super) async fn recover_pending(self: &Arc<Self>) -> Result<(), Error> {
        if !self.legacy.writable {
            return Ok(());
        }
        let Ok(mut state) = self.recovery.clone().try_lock_owned() else {
            return Ok(());
        };
        let writer = self.clone();
        // Dropping the caller or its deadline cannot release guards during storage work.
        let work = tokio::spawn(async move { writer.recover_batch(&mut state).await });
        tokio::time::timeout(Duration::from_secs(5), work)
            .await
            .map_err(|_| {
                Error::Invalid("cache publication recovery is still performing storage work")
            })?
            .map_err(|_| Error::Invalid("cache publication recovery task failed"))?
    }

    async fn recover_batch(self: &Arc<Self>, state: &mut State) -> Result<(), Error> {
        let folders = serde_json::to_string(
            &self
                .settings
                .folders
                .iter()
                .map(|f| &f.id)
                .collect::<Vec<_>>(),
        )
        .map_err(|_| Error::Invalid("cannot encode cache recovery folders"))?;
        let entries: Vec<Entry> = sqlx::query_as("SELECT Key AS key,Folder AS folder,ItemId AS item,Length AS size,Pinned AS pinned,Generation AS generation,ChunkSize AS chunk FROM Entries WHERE PendingBytes>0 AND Key>$1 AND Folder IN (SELECT value FROM json_each($2)) ORDER BY Key LIMIT 4")
            .bind(&state.after).bind(folders).fetch_all(&self.legacy.db).await?;
        let exhausted = entries.len() < ENTRIES_PER_PASS;
        let started = Instant::now();
        for entry in entries {
            if started.elapsed() >= Duration::from_secs(4) {
                return Ok(());
            }
            state.after.clone_from(&entry.key);
            // ponytail: keep at most 4096 scan cursors; a persistent work queue can
            // replace this bounded retry cache if thousands of entries need recovery.
            if state.entries.len() >= 4096 && !state.entries.contains_key(&entry.key) {
                state.entries.clear();
            }
            let progress = state.entries.entry(entry.key.clone()).or_default();
            match self.recover_entry(&entry, progress).await {
                Ok(true) => {
                    state.entries.remove(&entry.key);
                }
                Ok(false) => {}
                Err(error) => {
                    progress.restart();
                    let reason = error.to_string();
                    if progress.warning.as_ref() != Some(&reason) {
                        tracing::warn!(cache_key=%entry.key,folder=%entry.folder,reason,"Native cache publication recovery held");
                        progress.warning = Some(reason);
                    }
                }
            }
        }
        if exhausted {
            state.after.clear();
        }
        Ok(())
    }

    async fn recover_entry(
        self: &Arc<Self>,
        entry: &Entry,
        progress: &mut Progress,
    ) -> Result<bool, Error> {
        let item = Uuid::parse_str(&entry.item)
            .map_err(|_| Error::Invalid("invalid cache recovery item"))?;
        let generation = entry
            .generation
            .as_deref()
            .ok_or(Error::Invalid("missing cache recovery generation"))?;
        if entry.size <= 0 || !Legacy::matches(entry, item, generation, entry.size.cast_unsigned())
        {
            return Err(Error::Invalid("cache recovery identity is inconsistent"));
        }
        let Ok(_entry_guard) = self.legacy.entry_lock(&entry.key).try_read_owned() else {
            return Ok(false);
        };
        let Ok(_placement) = self.lock(entry.key.clone()).try_lock_owned() else {
            return Ok(false);
        };
        if self
            .state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .active
            .contains_key(&entry.key)
        {
            return Ok(false);
        }
        // Entry admission excludes retirement; re-read metadata after taking it.
        let Some(current) = self
            .legacy
            .find(item, generation, entry.size.cast_unsigned())
            .await?
        else {
            return Ok(false);
        };
        if current.folder != entry.folder || current.chunk != entry.chunk {
            return Err(Error::Invalid("cache recovery placement changed"));
        }
        let retired: bool =
            sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM RetiredEntries WHERE Key=$1)")
                .bind(&entry.key)
                .fetch_one(&self.legacy.db)
                .await?;
        if retired {
            return Err(Error::Invalid("retired cache publication remains held"));
        }
        let root = self
            .legacy
            .roots
            .get(&entry.folder)
            .cloned()
            .ok_or(Error::Invalid("cache recovery folder is unavailable"))?;
        let volume = root.clone();
        let identity = current.clone();
        let journal_lock = self.clone_lock(&entry.key);
        let (snapshot, records) = tokio::task::spawn_blocking(move || {
            let _journal = journal_lock.blocking_lock();
            read_journal(&volume, &identity)
        })
        .await
        .map_err(|_| Error::Invalid("cache recovery journal task failed"))??;
        let pending: i64 = sqlx::query_scalar("SELECT PendingBytes FROM Entries WHERE Key=$1")
            .bind(&entry.key)
            .fetch_one(&self.legacy.db)
            .await?;
        if pending <= 0 {
            return Ok(true);
        }
        if progress.snapshot != Some(snapshot) || progress.pending != pending {
            progress.restart();
            progress.snapshot = Some(snapshot);
            progress.pending = pending;
        }
        let stop = records
            .len()
            .min(progress.scanned.saturating_add(RECORDS_PER_PASS));
        // Compare bounded pages against the catalogue. Existing blocks are immutable.
        for page in records[progress.scanned..stop].chunks(256) {
            let offsets = serde_json::to_string(&page.iter().map(|r| r.offset).collect::<Vec<_>>())
                .map_err(|_| Error::Invalid("cannot encode cache recovery offsets"))?;
            let known: Vec<super::super::Block> = sqlx::query_as("SELECT Offset AS offset,Count AS count,Hash AS hash FROM Blocks WHERE Key=$1 AND Offset IN (SELECT value FROM json_each($2))")
                .bind(&entry.key).bind(offsets).fetch_all(&self.legacy.db).await?;
            let known: HashMap<_, _> = known.into_iter().map(|b| (b.offset, b)).collect();
            for record in page {
                if let Some(block) = known.get(&record.offset) {
                    if block.count != i64::from(record.count) || hex(&block.hash) != record.hash {
                        return Err(Error::Invalid(
                            "cache journal conflicts with a committed block",
                        ));
                    }
                } else {
                    progress.allocation = progress
                        .allocation
                        .checked_add(allocation(record))
                        .ok_or(Error::Invalid("cache recovery accounting overflow"))?;
                    if progress.candidates.len() < BLOCKS_PER_PASS {
                        progress.candidates.push(Journal {
                            offset: record.offset,
                            count: record.count,
                            hash: record.hash.clone(),
                        });
                    }
                }
            }
        }
        progress.scanned = stop;
        if stop < records.len() {
            return Ok(false);
        }
        let pending: i64 = sqlx::query_scalar("SELECT PendingBytes FROM Entries WHERE Key=$1")
            .bind(&entry.key)
            .fetch_one(&self.legacy.db)
            .await?;
        if pending <= 0 {
            return Ok(true);
        }
        if progress.candidates.is_empty() || progress.allocation > pending.cast_unsigned() {
            return Err(Error::Invalid(
                "pending cache accounting is not explained by complete journal records",
            ));
        }
        let candidates = std::mem::take(&mut progress.candidates);
        progress.restart();
        let mut recovered = 0;
        for record in candidates {
            let Ok(_block) = self
                .legacy
                .block_lock(&entry.key, record.offset.cast_unsigned() / BLOCK)
                .try_write_owned()
            else {
                continue;
            };
            let hash = decode_hash(&record.hash)?;
            if self
                .reconcile(
                    &current,
                    root.clone(),
                    record.offset.cast_unsigned(),
                    &hash,
                    allocation(&record),
                )
                .await?
            {
                recovered += 1;
            }
        }
        if recovered > 0 {
            tracing::info!(cache_key=%entry.key,blocks=recovered,"Native cache publication recovered");
        }
        let pending: i64 = sqlx::query_scalar("SELECT PendingBytes FROM Entries WHERE Key=$1")
            .bind(&entry.key)
            .fetch_one(&self.legacy.db)
            .await?;
        Ok(pending == 0)
    }
}

fn allocation(record: &Journal) -> u64 {
    u64::from(record.count.cast_unsigned())
        .next_multiple_of(65_536_u64)
        .saturating_add(4096)
}
fn decode_hash(text: &str) -> Result<[u8; 32], Error> {
    if text.len() != 64 || !text.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(Error::Invalid("invalid cache recovery checksum"));
    }
    let mut hash = [0; 32];
    for (i, byte) in hash.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&text[2 * i..2 * i + 2], 16)
            .map_err(|_| Error::Invalid("invalid cache recovery checksum"))?;
    }
    Ok(hash)
}
fn read_journal(root: &Root, entry: &Entry) -> Result<([u8; 32], Vec<Journal>), Error> {
    let dir = root.entry(&entry.key)?;
    verify_manifest(root, &dir, entry)?;
    let journal = leaf(&dir, "ranges.journal", root.device)?;
    if journal.metadata()?.len() > JOURNAL_LIMIT {
        return Err(Error::Invalid("cache recovery journal is oversized"));
    }
    let mut input = io::BufReader::new(journal);
    let mut total = 0_u64;
    let mut snapshot = Sha256::new();
    let mut records: Vec<Journal> = Vec::new();
    let mut seen = HashMap::new();
    loop {
        let mut line = String::new();
        let n = (&mut input).take(513).read_line(&mut line)?;
        if n == 0 {
            break;
        }
        total = total.saturating_add(n as u64);
        if total > JOURNAL_LIMIT || n > 512 || !line.ends_with('\n') {
            return Err(Error::Invalid(
                "cache recovery journal has an incomplete or oversized record",
            ));
        }
        snapshot.update(line.as_bytes());
        let mut record: Journal = serde_json::from_str(&line)
            .map_err(|_| Error::Invalid("invalid cache recovery journal record"))?;
        if record.offset < 0
            || record.offset.cast_unsigned() >= entry.size.cast_unsigned()
            || !record.offset.cast_unsigned().is_multiple_of(BLOCK)
            || u64::try_from(record.count)
                != Ok(BLOCK.min(entry.size.cast_unsigned() - record.offset.cast_unsigned()))
        {
            return Err(Error::Invalid(
                "cache recovery journal block is out of bounds",
            ));
        }
        decode_hash(&record.hash)?;
        record.hash.make_ascii_lowercase();
        if let Some(&index) = seen.get(&record.offset) {
            let prior: &Journal = &records[index];
            if prior.count != record.count || prior.hash != record.hash {
                return Err(Error::Invalid(
                    "cache recovery journal contains conflicting records",
                ));
            }
        } else {
            seen.insert(record.offset, records.len());
            records.push(record);
        }
    }
    root.validate()?;
    Ok((snapshot.finalize().into(), records))
}
