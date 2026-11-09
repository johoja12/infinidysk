//! Opt-in whole-entry eviction. Intent lives in the private Rust catalogue;
//! original `RetiredEntries` withdraws coverage and prevents v1 scanner resurrection.
use super::{Entry, Error, File, Root, Uuid, Writer, leaf, verify_manifest};
use crate::legacy::{Legacy, Maintenance};
use nix::dir::Dir;
use nix::fcntl::{OFlag, openat, renameat};
use nix::sys::stat::Mode;
use nix::unistd::{UnlinkatFlags, dup, unlinkat};
use sqlx::{FromRow, SqlitePool};
use std::collections::{HashMap, HashSet};
use std::os::unix::fs::MetadataExt as _;
use std::sync::Arc;
use std::time::Duration;

const BATCH: usize = 4096;
#[derive(Default)]
pub(super) struct State {
    pressured: HashSet<String>,
    cursors: HashMap<String, (i64, String)>,
}

#[derive(FromRow)]
struct Intent {
    key: String,
    folder: String,
    volume: String,
    identity: String,
    bytes: i64,
    nonce: String,
}
impl Intent {
    fn entry(&self) -> Result<Entry, Error> {
        let entry: Entry = serde_json::from_str(&self.identity)
            .map_err(|_| Error::Invalid("invalid compatible eviction identity"))?;
        let item = Uuid::parse_str(&entry.item)
            .map_err(|_| Error::Invalid("invalid compatible eviction item"))?;
        let generation = entry
            .generation
            .as_deref()
            .ok_or(Error::Invalid("missing eviction generation"))?;
        if self.bytes < 0
            || self.key != entry.key
            || self.folder != entry.folder
            || entry.pinned != 0
            || entry.size <= 0
            || entry.chunk < 0
            || (entry.chunk != 0
                && (entry.chunk % crate::BLOCK.cast_signed() != 0
                    || entry.chunk > 256_000_000_u64.next_multiple_of(crate::BLOCK).cast_signed()))
            || !Legacy::matches(&entry, item, generation, entry.size.cast_unsigned())
            || Uuid::parse_str(&self.nonce).is_err()
            || self.nonce.len() != 32
            || !self
                .nonce
                .bytes()
                .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
        {
            return Err(Error::Invalid(
                "compatible eviction identity does not match its intent",
            ));
        }
        Ok(entry)
    }
}

impl Writer {
    /// A deadline never cancels deletion. The owned task retains admission,
    /// original ownership and the entry guard until physical and metadata work ends.
    pub async fn maintain(self: &Arc<Self>, context: Maintenance) -> Result<(), Error> {
        if !self.settings.folders.iter().any(|f| f.eviction.is_some()) {
            return Ok(());
        }
        let Ok(mut pressure) = self.eviction.clone().try_lock_owned() else {
            return Ok(());
        };
        let writer = self.clone();
        let work =
            tokio::spawn(async move { writer.maintain_inner(&context, &mut pressure).await });
        tokio::time::timeout(Duration::from_secs(5), work)
            .await
            .map_err(|_| Error::Invalid("compatible eviction is still performing storage work"))?
            .map_err(|_| Error::Invalid("compatible eviction task failed"))?
    }

    async fn maintain_inner(
        self: &Arc<Self>,
        context: &Maintenance,
        state: &mut State,
    ) -> Result<(), Error> {
        let started = std::time::Instant::now();
        let mut deferred = None;
        let pending: Vec<Intent> = sqlx::query_as("SELECT key,folder,volume,identity,bytes,nonce FROM legacy_evictions_v1 ORDER BY attempted,folder,key LIMIT 32")
            .fetch_all(&context.cache.db).await?;
        for intent in pending {
            if started.elapsed() >= Duration::from_secs(4) {
                return deferred.map_or(Ok(()), Err);
            }
            if self
                .settings
                .folders
                .iter()
                .any(|f| f.id == intent.folder && f.eviction.is_some())
                && let Err(error) = self.resume(context, &intent).await
            {
                deferred = Some(error);
            }
            sqlx::query("UPDATE legacy_evictions_v1 SET attempted=(SELECT COALESCE(MAX(attempted),0)+1 FROM legacy_evictions_v1) WHERE key=$1 AND folder=$2 AND nonce=$3")
                .bind(&intent.key).bind(&intent.folder).bind(&intent.nonce).execute(&context.cache.db).await?;
        }
        // Recency is asynchronous. Never select a victim from stale/lost telemetry.
        if self
            .legacy
            .recency
            .as_ref()
            .is_some_and(|r| r.dropped() != 0)
        {
            return Err(Error::Invalid(
                "compatible recency was lost; eviction remains held",
            ));
        }
        if !self.recency_settled() {
            return deferred.map_or(Ok(()), Err);
        }
        for option in self
            .settings
            .folders
            .iter()
            .filter(|f| f.eviction.is_some())
        {
            let Some(policy) = &option.eviction else {
                continue;
            };
            let free = match self.eviction_free(&option.id).await {
                Ok(Some(free)) => free,
                Ok(None) => continue,
                Err(error) => {
                    deferred = Some(error);
                    continue;
                }
            };
            let mut used = self.folder_bytes(&option.id).await?;
            let low = option.quota_bytes / 100 * u64::from(policy.low_percent);
            let high = option.quota_bytes / 100 * u64::from(policy.high_percent);
            let required = self
                .device_reserve(
                    self.legacy
                        .roots
                        .get(&option.id)
                        .ok_or(Error::Invalid("missing eviction volume"))?
                        .device,
                )
                .saturating_add(crate::BLOCK)
                .saturating_add(super::OVERHEAD);
            let was_pressured = state.pressured.contains(&option.id);
            let mut pressured = if was_pressured {
                used > low || free < required
            } else {
                used >= high || free < required
            };
            if pressured {
                if !was_pressured {
                    state.cursors.remove(&option.id);
                }
                state.pressured.insert(option.id.clone());
            } else {
                state.pressured.remove(&option.id);
            }
            let cutoff =
                (crate::now() / 300_000).saturating_sub(i64::from(policy.max_age_days) * 288);
            if !pressured && policy.max_age_days == 0 {
                state.cursors.remove(&option.id);
                continue;
            }
            let after = state
                .cursors
                .get(&option.id)
                .cloned()
                .unwrap_or((i64::MIN, String::new()));
            let candidates: Vec<(String, i64)> = sqlx::query_as("SELECT Key,Access FROM Entries WHERE Folder=$1 AND Pinned=0 AND PendingBytes=0 AND Dirty=0 AND Length>0 AND (Access>$2 OR (Access=$2 AND Key>$3)) AND ($4 OR Access<$5) ORDER BY Access,Key LIMIT 64")
                .bind(&option.id).bind(after.0).bind(after.1).bind(pressured).bind(cutoff).fetch_all(&self.legacy.db).await?;
            let exhausted = candidates.len() < 64;
            for (key, access) in candidates {
                if started.elapsed() >= Duration::from_secs(4) {
                    return deferred.map_or(Ok(()), Err);
                }
                let aged = policy.max_age_days != 0 && access < cutoff;
                if !aged && !pressured {
                    continue;
                }
                let pending: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM legacy_evictions_v1")
                    .fetch_one(&context.cache.db)
                    .await?;
                if pending >= 256 {
                    return Err(Error::Invalid(
                        "too many compatible evictions are held; reconcile existing intents",
                    ));
                }
                state
                    .cursors
                    .insert(option.id.clone(), (access, key.clone()));
                match self.stage(context, &key, access, "policy").await {
                    Ok(true) => {}
                    Ok(false) => continue,
                    Err(error) => {
                        deferred = Some(error);
                        continue;
                    }
                }
                used = self.folder_bytes(&option.id).await?;
                let free = match self.eviction_free(&option.id).await {
                    Ok(Some(free)) => free,
                    Ok(None) => break,
                    Err(error) => {
                        deferred = Some(error);
                        break;
                    }
                };
                pressured = used > low || free < required;
                if !pressured {
                    state.pressured.remove(&option.id);
                }
            }
            if exhausted {
                state.cursors.remove(&option.id);
            }
        }
        deferred.map_or(Ok(()), Err)
    }

    fn recency_settled(&self) -> bool {
        self.legacy
            .recency
            .as_ref()
            .is_none_or(|r| r.pending() == 0 && r.dropped() == 0)
    }
    async fn folder_bytes(&self, id: &str) -> Result<u64, Error> {
        let bytes: i64 = sqlx::query_scalar(
            "SELECT COALESCE((SELECT Bytes FROM FolderTotals WHERE Folder=$1),0)",
        )
        .bind(id)
        .fetch_one(&self.legacy.db)
        .await?;
        u64::try_from(bytes).map_err(|_| Error::Invalid("negative compatible cache accounting"))
    }
    async fn eviction_free(&self, id: &str) -> Result<Option<u64>, Error> {
        let Some(root) = self.legacy.roots.get(id).cloned() else {
            return Ok(None);
        };
        let Some(probe) = self.probes.get(id) else {
            return Ok(None);
        };
        let Ok(permit) = probe.clone().try_acquire_owned() else {
            return Ok(None);
        };
        let reserve = self.device_reserve(root.device);
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            root.validate()?;
            let stat = nix::sys::statvfs::fstatvfs(&root.file)?;
            if reserve > crate::filesystem::bytes(stat.blocks(), stat.fragment_size()) {
                return Err(Error::Invalid(
                    "cache free-space reserve exceeds filesystem capacity; eviction held",
                ));
            }
            if stat.flags().contains(nix::sys::statvfs::FsFlags::ST_RDONLY) {
                return Ok(None);
            }
            Ok(Some(crate::filesystem::bytes(
                stat.blocks_available(),
                stat.fragment_size(),
            )))
        })
        .await
        .map_err(|_| Error::Invalid("compatible eviction capacity probe failed"))?
    }

    async fn stage(
        self: &Arc<Self>,
        context: &Maintenance,
        key: &str,
        access: i64,
        reason: &'static str,
    ) -> Result<bool, Error> {
        let row: Option<Entry> = sqlx::query_as("SELECT Key AS key,Folder AS folder,ItemId AS item,Length AS size,Pinned AS pinned,Generation AS generation,ChunkSize AS chunk FROM Entries WHERE Key=$1")
            .bind(key).fetch_optional(&self.legacy.db).await?;
        let Some(entry) = row else {
            return Ok(false);
        };
        let Some(root) = self.legacy.roots.get(&entry.folder) else {
            return Ok(false);
        };
        let item =
            Uuid::parse_str(&entry.item).map_err(|_| Error::Invalid("invalid eviction item"))?;
        let Some(mark) = context.claim(item) else {
            return Ok(false);
        };
        let Ok(guard) = self.legacy.entry_lock(key).try_write_owned() else {
            return Ok(false);
        };
        if !self.recency_settled() {
            return Ok(false);
        }
        let volume = root.clone();
        tokio::task::spawn_blocking(move || volume.validate())
            .await
            .map_err(|_| Error::Invalid("eviction volume validation failed"))??;
        let bytes: Option<i64> = sqlx::query_scalar("SELECT Bytes FROM Entries WHERE Key=$1 AND Access=$2 AND Pinned=0 AND PendingBytes=0 AND Dirty=0")
            .bind(key).bind(access).fetch_optional(&self.legacy.db).await?;
        let Some(bytes) = bytes else {
            return Ok(false);
        };
        let mut identity = serde_json::to_value(&entry)
            .map_err(|_| Error::Invalid("cannot encode eviction identity"))?;
        let serde_json::Value::Object(fields) = &mut identity else {
            return Err(Error::Invalid("invalid eviction identity"));
        };
        fields.insert("_reason".into(), serde_json::Value::String(reason.into()));
        let intent = Intent {
            key: key.into(),
            folder: entry.folder.clone(),
            volume: root.volume.clone(),
            identity: serde_json::to_string(&identity)
                .map_err(|_| Error::Invalid("cannot encode eviction identity"))?,
            bytes,
            nonce: Uuid::new_v4().simple().to_string(),
        };
        intent.entry()?;
        let inserted = sqlx::query("INSERT OR IGNORE INTO legacy_evictions_v1(key,folder,volume,identity,bytes,nonce) VALUES($1,$2,$3,$4,$5,$6)")
            .bind(&intent.key).bind(&intent.folder).bind(&intent.volume).bind(&intent.identity).bind(bytes).bind(&intent.nonce)
            .execute(&context.cache.db).await?.rows_affected();
        if inserted == 0 {
            return Ok(false);
        }
        self.point("eviction_intent")?;
        let mut tx = self.legacy.db.begin().await?;
        let retired = sqlx::query("INSERT INTO RetiredEntries(Key,Folder,Bytes,ItemId,DisplayName,Length,VerifiedBytes,AccessCount,LastAccessUnix) SELECT Key,Folder,Bytes,ItemId,DisplayName,Length,VerifiedBytes,AccessCount,Access*300 FROM Entries WHERE Key=$1 AND Folder=$2 AND Access=$3 AND Pinned=0 AND PendingBytes=0 AND Dirty=0 AND NOT EXISTS(SELECT 1 FROM RetiredEntries WHERE Key=$1 AND Folder=$2)")
            .bind(key).bind(&entry.folder).bind(access).execute(&mut *tx).await?.rows_affected();
        if retired == 0 {
            tx.rollback().await?;
            forget(&context.cache.db, &intent).await?;
            return Ok(false);
        }
        sqlx::query("DELETE FROM Entries WHERE Key=$1 AND Folder=$2")
            .bind(key)
            .bind(&entry.folder)
            .execute(&mut *tx)
            .await?;
        tx.commit().await?;
        self.point("eviction_staged")?;
        // Release only after coverage was withdrawn. Resume reacquires both guards.
        drop((guard, mark));
        self.resume(context, &intent).await
    }

    async fn resume(
        self: &Arc<Self>,
        context: &Maintenance,
        intent: &Intent,
    ) -> Result<bool, Error> {
        let entry = intent.entry()?;
        let Some(root) = self
            .legacy
            .roots
            .get(&entry.folder)
            .filter(|r| r.volume == intent.volume)
            .cloned()
        else {
            return Ok(false);
        };
        let item =
            Uuid::parse_str(&entry.item).map_err(|_| Error::Invalid("invalid eviction item"))?;
        let Some(mark) = context.claim(item) else {
            return Ok(false);
        };
        let Ok(guard) = self.legacy.entry_lock(&entry.key).try_write_owned() else {
            return Ok(false);
        };
        let current: bool =
            sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM Entries WHERE Key=$1 AND Folder=$2)")
                .bind(&entry.key)
                .bind(&entry.folder)
                .fetch_one(&self.legacy.db)
                .await?;
        let retired: Option<(i64, String, i64)> = sqlx::query_as(
            "SELECT Bytes,ItemId,Length FROM RetiredEntries WHERE Key=$1 AND Folder=$2",
        )
        .bind(&entry.key)
        .bind(&entry.folder)
        .fetch_optional(&self.legacy.db)
        .await?;
        if current {
            // A provisional intent never withdrew coverage. A restored/new current
            // copy must not be mistaken for the old deletion target.
            if retired.is_none() {
                let volume = root.clone();
                let target = entry.clone();
                let nonce = intent.nonce.clone();
                let quarantined = tokio::task::spawn_blocking(move || {
                    volume.validate()?;
                    let Some(version) = open_dir(&volume.file, "v1", volume.device)? else {
                        return Ok::<_, Error>(false);
                    };
                    let Some(shard) = open_dir(&version, &target.key[..2], volume.device)? else {
                        return Ok(false);
                    };
                    Ok(open_dir(&shard, &format!("rust-evict-{nonce}"), volume.device)?.is_some())
                })
                .await
                .map_err(|_| Error::Invalid("eviction conflict probe failed"))??;
                if quarantined {
                    return Err(Error::Invalid(
                        "recreated entry conflicts with retained eviction intent; review required",
                    ));
                }
                forget(&context.cache.db, intent).await?;
            }
            return Ok(false);
        }
        let authorised = retired.as_ref().is_some_and(|(bytes, item, size)| {
            *bytes == intent.bytes && item == &entry.item && *size == entry.size
        });
        if retired.is_some() && !authorised {
            return Ok(false);
        }
        let nonce = intent.nonce.clone();
        let writer = self.clone();
        let target = entry.clone();
        let physical = tokio::task::spawn_blocking(move || {
            let result = remove(&writer, &root, &target, &nonce, authorised);
            (result, (guard, mark))
        })
        .await
        .map_err(|_| Error::Invalid("compatible eviction physical task failed"))?;
        let (result, _guards) = physical;
        if !result? {
            return Ok(false);
        }
        if authorised {
            let removed = sqlx::query("DELETE FROM RetiredEntries WHERE Key=$1 AND Folder=$2 AND Bytes=$3 AND ItemId=$4 AND Length=$5 AND NOT EXISTS(SELECT 1 FROM Entries WHERE Key=$1 AND Folder=$2)")
                .bind(&entry.key).bind(&entry.folder).bind(intent.bytes).bind(&entry.item).bind(entry.size)
                .execute(&self.legacy.db).await?.rows_affected();
            if removed == 0 {
                return Ok(false);
            }
        }
        self.point("eviction_metadata")?;
        let mut tx = context.cache.db.begin().await?;
        let reason = serde_json::from_str::<serde_json::Value>(&intent.identity)
            .ok()
            .map_or("legacy:policy", |v| {
                if v.get("_reason").and_then(serde_json::Value::as_str) == Some("manual") {
                    "legacy:manual"
                } else {
                    "legacy:policy"
                }
            });
        sqlx::query("INSERT INTO evictions(item,revision,bytes,at,reason) VALUES($1,$2,$3,$4,$5)")
            .bind(&entry.item)
            .bind(&entry.key)
            .bind(intent.bytes)
            .bind(crate::now())
            .bind(reason)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM legacy_evictions_v1 WHERE key=$1 AND folder=$2 AND nonce=$3")
            .bind(&entry.key)
            .bind(&entry.folder)
            .bind(&intent.nonce)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM evictions WHERE rowid NOT IN(SELECT rowid FROM evictions ORDER BY at DESC,rowid DESC LIMIT 10000)").execute(&mut *tx).await?;
        tx.commit().await?;
        Ok(true)
    }
}

async fn forget(db: &SqlitePool, intent: &Intent) -> Result<(), Error> {
    sqlx::query("DELETE FROM legacy_evictions_v1 WHERE key=$1 AND folder=$2 AND nonce=$3")
        .bind(&intent.key)
        .bind(&intent.folder)
        .bind(&intent.nonce)
        .execute(db)
        .await?;
    Ok(())
}
fn open_dir(parent: &File, name: &str, device: u64) -> Result<Option<File>, Error> {
    let fd = match openat(
        parent,
        name,
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    ) {
        Ok(fd) => fd,
        Err(nix::errno::Errno::ENOENT) => {
            parent.sync_all()?;
            return Ok(None);
        }
        Err(e) => return Err(e.into()),
    };
    let file = File::from(fd);
    if file.metadata()?.dev() != device {
        return Err(Error::Invalid("eviction directory crosses devices"));
    }
    Ok(Some(file))
}
fn remove(
    writer: &Writer,
    root: &Root,
    entry: &Entry,
    nonce: &str,
    authorised: bool,
) -> Result<bool, Error> {
    root.validate()?;
    let Some(version) = open_dir(&root.file, "v1", root.device)? else {
        return Ok(true);
    };
    let Some(shard) = open_dir(&version, &entry.key[..2], root.device)? else {
        return Ok(true);
    };
    let name = format!("rust-evict-{nonce}");
    let original = open_dir(&shard, &entry.key, root.device)?;
    let quarantined = open_dir(&shard, &name, root.device)?;
    if !authorised {
        if original.is_some() || quarantined.is_some() {
            return Ok(false);
        }
        root.validate()?;
        return Ok(true);
    }
    let dir = match (original, quarantined) {
        (Some(_), Some(_)) => {
            return Err(Error::Invalid(
                "eviction conflicts with a recreated cache directory",
            ));
        }
        (Some(original), None) => {
            verify_manifest(root, &original, entry)?;
            root.validate()?;
            renameat(&shard, entry.key.as_str(), &shard, name.as_str())?;
            shard.sync_all()?;
            writer.point("eviction_renamed")?;
            original
        }
        (None, Some(dir)) => dir,
        (None, None) => {
            root.validate()?;
            return Ok(true);
        }
    };
    let has_manifest = match leaf(&dir, "manifest.json", root.device) {
        Ok(_) => {
            verify_manifest(root, &dir, entry)?;
            true
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => false,
        Err(e) => return Err(e.into()),
    };
    let mut contents = Dir::from_fd(dup(&dir)?)?;
    let mut payloads = Vec::new();
    let mut metadata = HashSet::new();
    for file in contents.iter() {
        let file = file?;
        let filename = file
            .file_name()
            .to_str()
            .map_err(|_| Error::Invalid("unknown eviction leaf"))?;
        if matches!(filename, "." | "..") {
            continue;
        }
        if matches!(
            filename,
            "manifest.json" | "ranges.journal" | "ranges.checkpoint.tmp"
        ) {
            metadata.insert(filename.to_owned());
        } else {
            let valid = if entry.chunk == 0 {
                filename == "content.data"
            } else {
                filename
                    .strip_prefix("chunk-")
                    .and_then(|s| s.strip_suffix(".data"))
                    .filter(|s| {
                        s.len() == 16
                            && s.bytes()
                                .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
                    })
                    .and_then(|s| u64::from_str_radix(s, 16).ok())
                    .is_some_and(|index| {
                        index
                            < entry
                                .size
                                .cast_unsigned()
                                .div_ceil(entry.chunk.cast_unsigned())
                    })
            };
            if !valid {
                return Err(Error::Invalid(
                    "unknown file in eviction directory; deletion held",
                ));
            }
            payloads.push(filename.to_owned());
        }
        leaf(&dir, filename, root.device)?;
        if payloads.len() == BATCH {
            break;
        }
    }
    if !has_manifest && (!payloads.is_empty() || !metadata.is_empty()) {
        return Err(Error::Invalid(
            "eviction manifest is missing; deletion held",
        ));
    }
    for file in &payloads {
        root.validate()?;
        leaf(&dir, file, root.device)?;
        unlinkat(&dir, file.as_str(), UnlinkatFlags::NoRemoveDir)?;
        writer.point("eviction_unlinked")?;
    }
    dir.sync_all()?;
    if payloads.len() == BATCH {
        return Ok(false);
    }
    for file in ["ranges.checkpoint.tmp", "ranges.journal", "manifest.json"] {
        if metadata.contains(file) {
            root.validate()?;
            leaf(&dir, file, root.device)?;
            unlinkat(&dir, file, UnlinkatFlags::NoRemoveDir)?;
        }
    }
    writer.point("eviction_manifest_removed")?;
    dir.sync_all()?;
    unlinkat(&shard, name.as_str(), UnlinkatFlags::RemoveDir)?;
    shard.sync_all()?;
    root.validate()?;
    writer.point("eviction_removed")?;
    Ok(true)
}

impl Writer {
    /// Manual deletion shares policy eviction's admission, journal and physical protocol.
    /// It requires explicit eviction permission on this folder, even for manual actions.
    pub async fn manual_evict(
        self: &Arc<Self>,
        identity: crate::legacy::EntryIdentity,
        context: Maintenance,
    ) -> Result<bool, Error> {
        if context.cache.recovery_mode() {
            return Ok(false);
        }
        if !self
            .settings
            .folders
            .iter()
            .any(|f| f.id == identity.folder && f.eviction.is_some())
        {
            return Ok(false);
        }
        let Ok(serial) = self.eviction.clone().try_lock_owned() else {
            return Ok(false);
        };
        let writer = self.clone();
        let task = tokio::spawn(async move {
            writer.legacy.flush_recency().await?;
            let pending: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM legacy_evictions_v1")
                .fetch_one(&context.cache.db)
                .await?;
            if pending >= 256 {
                return Err(Error::Invalid(
                    "too many compatible evictions are held; reconcile existing intents",
                ));
            }
            if writer.legacy.exact_entry(&identity).await?.is_none() {
                return Ok(false);
            }
            let access:Option<i64>=sqlx::query_scalar("SELECT Access FROM Entries WHERE Key=$1 AND Pinned=0 AND PendingBytes=0 AND Dirty=0").bind(&identity.key).fetch_optional(&writer.legacy.db).await?;
            // The task owns serialization even if its HTTP caller disconnects.
            let _serial = serial;
            match access {
                Some(access) => {
                    writer
                        .stage(&context, &identity.key, access, "manual")
                        .await
                }
                None => Ok(false),
            }
        });
        tokio::time::timeout(std::time::Duration::from_secs(5), task)
            .await
            .map_err(|_| {
                Error::Invalid("cache eviction is still running; refresh before retrying")
            })?
            .map_err(|_| Error::Invalid("cache eviction task failed"))?
    }
}
