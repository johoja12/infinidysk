//! Final-media bytes, below delivery: checksummed blocks in bounded chunk files.
//! The catalogue stays on local storage; browsing never scans payload volumes.
mod diagnostics;
mod filesystem;
mod manifest;
mod recency;
pub use manifest::{
    Candidate as RecoveryCandidate, Page as RecoveryPage, Report as RecoveryReport,
};
mod traffic;
pub use traffic::Stats as IoStats;
pub mod legacy;
pub mod repair;
pub use diagnostics::{Eviction, FolderDiagnostic, Probe};

use std::collections::{HashMap, HashSet};
use std::fs::{File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::unix::fs::{FileExt, MetadataExt, OpenOptionsExt};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, PoisonError, Weak};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use bytes::Bytes;
use nix::fcntl::{Flock, FlockArg, OFlag, openat};
use nix::sys::stat::Mode;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use sqlx::SqlitePool;
use sqlx::sqlite::{SqliteConnectOptions, SqliteJournalMode, SqlitePoolOptions, SqliteSynchronous};
use tokio::sync::{Mutex as AsyncMutex, Notify, OwnedSemaphorePermit, Semaphore};
use utoipa::ToSchema;
use uuid::Uuid;

pub const BLOCK_BYTES: usize = 4 * 1024 * 1024;
pub const BLOCK: u64 = BLOCK_BYTES as u64;
const MB: u64 = 1_000_000;
const OWNER: &str = ".infinidysk-native-v2";
const SCHEMA: i64 = 1;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("native cache I/O failed")]
    Io(#[from] io::Error),
    #[error("native cache filesystem operation failed")]
    System(#[from] nix::errno::Errno),
    #[error("native cache catalogue failed")]
    Db(#[from] sqlx::Error),
    #[error("{0}")]
    Invalid(&'static str),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as = NativeCacheSettings)]
pub struct Settings {
    pub enabled: bool,
    /// Explicit reconstruction mode: existing manifests may restore folder ownership; payload mutation stays off.
    pub recovery_mode: bool,
    pub metadata_path: String,
    pub folders: Vec<FolderSettings>,
    /// Offline legacy payloads are read in place; new writes never target these roots.
    pub legacy: Option<legacy::Settings>,
    pub buffer_mb: u32,
    pub min_file_mb: u32,
    /// New entries retain this layout even after the configured value changes.
    pub chunk_mb: u32,
    pub io_timeout_ms: u32,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: false,
            recovery_mode: false,
            metadata_path: "/config/native-cache-v2".to_owned(),
            folders: vec![],
            legacy: None,
            buffer_mb: 32,
            min_file_mb: 100,
            chunk_mb: 64,
            io_timeout_ms: 1000,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as = NativeCacheFolder)]
pub struct FolderSettings {
    pub id: Uuid,
    pub path: String,
    pub enabled: bool,
    pub read_only: bool,
    pub priority: i32,
    pub quota_bytes: u64,
    pub min_free_bytes: u64,
    pub max_age_hours: u32,
    pub high_percent: u8,
    pub low_percent: u8,
}
impl Default for FolderSettings {
    fn default() -> Self {
        Self {
            id: Uuid::nil(),
            path: String::new(),
            enabled: true,
            read_only: false,
            priority: 0,
            quota_bytes: 100_000_000_000,
            min_free_bytes: 1_000_000_000,
            max_age_hours: 0,
            high_percent: 90,
            low_percent: 80,
        }
    }
}
impl Settings {
    pub fn validate(&self) -> Result<(), Error> {
        if !(9..=256).contains(&self.buffer_mb) {
            return Err(Error::Invalid(
                "native cache buffer budget must be 9-256 MB, including a reserved playback slot",
            ));
        }
        if !(4..=256).contains(&self.chunk_mb) || !self.chunk_mb.is_multiple_of(4) {
            return Err(Error::Invalid(
                "native cache chunk target must be 4-256 MB in steps of 4",
            ));
        }
        if !(100..=30_000).contains(&self.io_timeout_ms) {
            return Err(Error::Invalid(
                "native cache storage wait must be 100-30000 ms",
            ));
        }
        if self.folders.len() > 64 || !Path::new(&self.metadata_path).is_absolute() {
            return Err(Error::Invalid(
                "native cache requires an absolute local metadata path and at most 64 folders",
            ));
        }
        let mut ids = HashSet::new();
        let mut paths = HashSet::new();
        for f in &self.folders {
            let path = Path::new(&f.path);
            if f.id.is_nil()
                || !ids.insert(f.id)
                || !path.is_absolute()
                || !paths.insert(path)
                || f.quota_bytes < BLOCK
                || f.quota_bytes > i64::MAX.cast_unsigned()
                || f.low_percent == 0
                || f.low_percent >= f.high_percent
                || f.high_percent > 100
                || Path::new(&self.metadata_path).starts_with(path)
            {
                return Err(Error::Invalid(
                    "native cache folders require unique ids/paths, valid quotas/watermarks and separate metadata",
                ));
            }
            if self.folders.iter().any(|other| {
                other.id != f.id
                    && (path.starts_with(&other.path) || Path::new(&other.path).starts_with(path))
            }) {
                return Err(Error::Invalid("native cache folders must not overlap"));
            }
        }
        if let Some(legacy) = &self.legacy {
            legacy.validate(self)?;
        }
        if self.enabled && !self.folders.iter().any(|f| f.enabled) && self.legacy.is_none() {
            return Err(Error::Invalid("enable at least one native cache folder"));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, Hash, PartialEq, Eq)]
pub struct Key {
    item: Uuid,
    revision: String,
    size: u64,
}
impl Key {
    /// Hash source metadata, not paths or titles. A new revision cannot hit old bytes.
    pub fn new(item: Uuid, metadata: &[u8], size: u64) -> Self {
        Self::from_fingerprint(item, Self::fingerprint(metadata), size)
    }
    pub fn fingerprint(metadata: &[u8]) -> [u8; 32] {
        Sha256::digest(metadata).into()
    }
    pub fn from_fingerprint(item: Uuid, fingerprint: [u8; 32], size: u64) -> Self {
        let mut hash = Sha256::new();
        hash.update(fingerprint);
        hash.update(size.to_le_bytes());
        Self {
            item,
            revision: hex(&hash.finalize()),
            size,
        }
    }
    pub const fn item(&self) -> Uuid {
        self.item
    }
    pub const fn size(&self) -> u64 {
        self.size
    }
    pub fn revision(&self) -> &str {
        &self.revision
    }
    fn from_entry(entry: &Entry) -> Result<Self, Error> {
        if entry.revision.len() != 64 || !entry.revision.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(Error::Invalid("invalid cache revision"));
        }
        Ok(Self {
            item: entry.item,
            revision: entry.revision.clone(),
            size: entry.size_bytes,
        })
    }
    fn id(&self) -> String {
        format!("{}-{}", self.item.simple(), self.revision)
    }
}

struct Folder {
    settings: FolderSettings,
    root: File,
    _lock: Flock<File>,
    device: u64,
    inode: u64,
    marker: String,
    writes: Arc<AsyncMutex<()>>,
    pressure: AtomicU64,
}
impl Folder {
    fn validate(&self) -> io::Result<()> {
        let meta = std::fs::symlink_metadata(&self.settings.path)?;
        if !meta.is_dir() || meta.dev() != self.device || meta.ino() != self.inode {
            return Err(io::Error::other("native cache folder identity changed"));
        }
        let file = File::from(openat(
            &self.root,
            OWNER,
            OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
            Mode::empty(),
        )?);
        if !file.metadata()?.is_file() || file.metadata()?.nlink() != 1 {
            return Err(io::Error::other("invalid cache ownership marker"));
        }
        let mut marker = String::new();
        file.take(100).read_to_string(&mut marker)?;
        if marker != self.marker {
            return Err(io::Error::other("native cache ownership marker changed"));
        }
        Ok(())
    }
    fn file(&self, name: &str, write: bool) -> io::Result<File> {
        // A missing mount or ownership marker is storage uncertainty, not a missing
        // payload. Keep it distinct from NotFound returned by the payload open below.
        self.validate().map_err(io::Error::other)?;
        let flags = if write {
            OFlag::O_RDWR | OFlag::O_CREAT
        } else {
            OFlag::O_RDONLY
        };
        let file = File::from(openat(
            &self.root,
            name,
            flags | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
            Mode::S_IRUSR | Mode::S_IWUSR,
        )?);
        let meta = file.metadata()?;
        if !meta.is_file() || meta.nlink() != 1 {
            return Err(io::Error::other(
                "cache payload is not a private regular file",
            ));
        }
        Ok(file)
    }
}

#[derive(Default)]
struct Live {
    readers: HashMap<Key, usize>,
    evicting: HashSet<Key>,
    legacy_evicting: HashSet<Uuid>,
}
/// Protects an entry from eviction for the whole read/warm, including client pauses.
pub struct Lease {
    key: Key,
    live: Arc<Mutex<Live>>,
}
impl Lease {
    pub const fn key(&self) -> &Key {
        &self.key
    }
}
impl Drop for Lease {
    fn drop(&mut self) {
        let mut live = self.live.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(count) = live.readers.get_mut(&self.key) {
            *count -= 1;
            if *count == 0 {
                live.readers.remove(&self.key);
            }
        }
    }
}

pub struct Cache {
    settings: Settings,
    db: SqlitePool,
    recency: recency::Updates,
    read_timeouts: AtomicU64,
    folders: Vec<Arc<Folder>>,
    live: Arc<Mutex<Live>>,
    entries: Mutex<HashMap<String, Weak<AsyncMutex<()>>>>,
    slots: Arc<Semaphore>,
    io: Arc<traffic::Counters>,
    maintenance: Notify,
    hits: AtomicU64,
    legacy_hits: AtomicU64,
    misses: AtomicU64,
    skipped: AtomicU64,
    failed: AtomicU64,
    legacy: OnceLock<Arc<dyn legacy::Reader>>,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as = NativeCacheEntry)]
pub struct Entry {
    pub item: Uuid,
    pub revision: String,
    pub size_bytes: u64,
    pub cached_bytes: u64,
    pub folder_id: Uuid,
    pub last_used_ms: i64,
    pub pinned: bool,
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as = NativeCacheStats)]
pub struct Stats {
    pub files: u64,
    pub bytes: u64,
    pub hits: u64,
    pub legacy_hits: u64,
    pub legacy_written_bytes: u64,
    pub misses: u64,
    pub skipped: u64,
    pub failed: u64,
    pub io: IoStats,
    pub read_timeouts: u64,
    pub recency_pending: usize,
    pub recency_dropped: u64,
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as = NativeCacheCoverage)]
pub struct Coverage {
    pub start: u64,
    pub end: u64,
}

impl Cache {
    pub async fn open(settings: Settings) -> Result<Arc<Self>, Error> {
        settings.validate()?;
        if let Some(legacy) = &settings.legacy {
            legacy.check_isolation(&settings)?;
        }
        tokio::fs::create_dir_all(&settings.metadata_path).await?;
        let metadata = std::fs::canonicalize(&settings.metadata_path)?;
        if crate::filesystem::remote(&metadata)? {
            return Err(Error::Invalid(
                "native cache catalogue must be on local storage",
            ));
        }
        let dbpath = metadata.join("catalogue.sqlite");
        let opts = SqliteConnectOptions::new()
            .filename(&dbpath)
            .create_if_missing(true)
            .foreign_keys(true)
            .journal_mode(SqliteJournalMode::Wal)
            .synchronous(SqliteSynchronous::Full)
            .busy_timeout(Duration::from_secs(5));
        let db = SqlitePoolOptions::new()
            .max_connections(4)
            .connect_with(opts)
            .await?;
        let version: i64 = sqlx::query_scalar("PRAGMA user_version")
            .fetch_one(&db)
            .await?;
        if version > SCHEMA {
            return Err(Error::Invalid(
                "native cache catalogue is newer than this binary",
            ));
        }
        for sql in [
            "CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY,path TEXT NOT NULL,device TEXT NOT NULL,inode TEXT NOT NULL,marker TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS entries(key TEXT PRIMARY KEY,item TEXT NOT NULL,revision TEXT NOT NULL,size BIGINT NOT NULL,folder TEXT NOT NULL,chunk BIGINT NOT NULL,used BIGINT NOT NULL,pinned INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE IF NOT EXISTS blocks(key TEXT NOT NULL REFERENCES entries(key) ON DELETE CASCADE,block BIGINT NOT NULL,len BIGINT NOT NULL,hash TEXT NOT NULL,valid INTEGER NOT NULL DEFAULT 1,PRIMARY KEY(key,block))",
            "CREATE INDEX IF NOT EXISTS cache_entries_eviction ON entries(folder,pinned,used)",
            "CREATE TABLE IF NOT EXISTS evictions(item TEXT NOT NULL,revision TEXT NOT NULL,bytes BIGINT NOT NULL,at BIGINT NOT NULL,reason TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS legacy_evictions_v1(key TEXT NOT NULL,folder TEXT NOT NULL,volume TEXT NOT NULL,identity TEXT NOT NULL,bytes BIGINT NOT NULL,nonce TEXT NOT NULL,attempted BIGINT NOT NULL DEFAULT 0,PRIMARY KEY(key,folder))",
            "CREATE TABLE IF NOT EXISTS repair_lineage(slot INTEGER PRIMARY KEY CHECK(slot=1),ledger_id TEXT NOT NULL,epoch BIGINT NOT NULL CHECK(epoch>=0))",
            "PRAGMA user_version=1",
        ] {
            sqlx::query(sql).execute(&db).await?;
        }
        let mut folders = Vec::new();
        for f in settings.folders.iter().filter(|f| f.enabled) {
            let path = Path::new(&f.path);
            let resolved = std::fs::canonicalize(path)?;
            if resolved != path
                || metadata.starts_with(&resolved)
                || resolved.starts_with(&metadata)
            {
                return Err(Error::Invalid(
                    "native cache paths must be canonical, separate directories",
                ));
            }
            let root = OpenOptions::new()
                .read(true)
                .custom_flags(OFlag::O_DIRECTORY.bits() | OFlag::O_NOFOLLOW.bits())
                .open(path)?;
            let meta = root.metadata()?;
            let mut registered: Option<(String, String, String, String)> =
                sqlx::query_as("SELECT path,device,inode,marker FROM folders WHERE id=$1")
                    .bind(f.id.to_string())
                    .fetch_optional(&db)
                    .await?;
            let mut adopted = false;
            if registered.is_none() && settings.recovery_mode {
                let marker = manifest::ownership(&root, f)?;
                registered = Some((
                    f.path.clone(),
                    meta.dev().to_string(),
                    meta.ino().to_string(),
                    marker,
                ));
                adopted = true;
            }
            // Validate identity before any create/open-for-write, including the lock.
            if let Some((registered_path, dev, ino, marker)) = &registered {
                if registered_path != &f.path
                    || dev != &meta.dev().to_string()
                    || ino != &meta.ino().to_string()
                {
                    return Err(Error::Invalid(
                        "native cache folder identity differs from its catalogue; restore its mount",
                    ));
                }
                let owner = File::from(openat(
                    &root,
                    OWNER,
                    OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
                    Mode::empty(),
                )?);
                if !owner.metadata()?.is_file() || owner.metadata()?.nlink() != 1 {
                    return Err(Error::Invalid("invalid cache ownership marker"));
                }
                let mut contents = String::new();
                owner.take(100).read_to_string(&mut contents)?;
                if &contents != marker {
                    return Err(Error::Invalid("native cache ownership marker changed"));
                }
            } else {
                if f.read_only {
                    return Err(Error::Invalid(
                        "read-only cache folders must already be registered",
                    ));
                }
                if std::fs::read_dir(path)?.next().is_some() {
                    return Err(Error::Invalid(
                        "new native cache folder must be empty; existing cache data needs its original catalogue",
                    ));
                }
            }
            let lock_flags = if f.read_only {
                OFlag::O_RDONLY
            } else if registered.is_none() {
                OFlag::O_RDWR | OFlag::O_CREAT
            } else {
                OFlag::O_RDWR
            };
            let lockfile = File::from(openat(
                &root,
                ".lock",
                lock_flags | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
                Mode::S_IRUSR | Mode::S_IWUSR,
            )?);
            let lock = Flock::lock(lockfile, FlockArg::LockExclusiveNonblock)
                .map_err(|(_, e)| io::Error::from(e))?;
            let marker = if let Some((registered_path, dev, ino, marker)) = registered {
                if registered_path != f.path
                    || dev != meta.dev().to_string()
                    || ino != meta.ino().to_string()
                {
                    return Err(Error::Invalid(
                        "native cache folder identity differs from its catalogue; restore its mount",
                    ));
                }
                marker
            } else {
                if f.read_only {
                    return Err(Error::Invalid(
                        "read-only cache folders must already be registered",
                    ));
                }
                if std::fs::read_dir(path)?.any(|e| e.map_or(true, |e| e.file_name() != ".lock")) {
                    return Err(Error::Invalid("new native cache folder must be empty"));
                }
                if path.join(OWNER).exists() {
                    return Err(Error::Invalid(
                        "native cache folder belongs to another catalogue",
                    ));
                }
                let marker = Uuid::new_v4().to_string();
                let mut owner = File::from(openat(
                    &root,
                    OWNER,
                    OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW,
                    Mode::S_IRUSR | Mode::S_IWUSR,
                )?);
                owner.write_all(marker.as_bytes())?;
                owner.sync_all()?;
                root.sync_all()?;
                sqlx::query(
                    "INSERT INTO folders(id,path,device,inode,marker) VALUES($1,$2,$3,$4,$5)",
                )
                .bind(f.id.to_string())
                .bind(&f.path)
                .bind(meta.dev().to_string())
                .bind(meta.ino().to_string())
                .bind(&marker)
                .execute(&db)
                .await?;
                marker
            };
            if adopted {
                sqlx::query(
                    "INSERT INTO folders(id,path,device,inode,marker) VALUES($1,$2,$3,$4,$5)",
                )
                .bind(f.id.to_string())
                .bind(&f.path)
                .bind(meta.dev().to_string())
                .bind(meta.ino().to_string())
                .bind(&marker)
                .execute(&db)
                .await?;
            }
            let folder = Folder {
                settings: f.clone(),
                root,
                _lock: lock,
                device: meta.dev(),
                inode: meta.ino(),
                marker,
                writes: Arc::default(),
                pressure: AtomicU64::new(0),
            };
            folder.validate()?;
            folders.push(Arc::new(folder));
        }
        folders.sort_by_key(|f| std::cmp::Reverse(f.settings.priority));
        let recency = recency::Updates::start(db.clone());
        Ok(Arc::new(Self {
            recency,
            read_timeouts: AtomicU64::new(0),
            slots: Arc::new(Semaphore::new(
                (u64::from(settings.buffer_mb) * MB / BLOCK).max(1) as usize,
            )),
            settings,
            db,
            folders,
            live: Arc::default(),
            entries: Mutex::default(),
            maintenance: Notify::new(),
            io: Arc::default(),
            hits: AtomicU64::new(0),
            legacy_hits: AtomicU64::new(0),
            misses: AtomicU64::new(0),
            skipped: AtomicU64::new(0),
            failed: AtomicU64::new(0),
            legacy: OnceLock::new(),
        }))
    }
    pub async fn repair_lineage(&self) -> Result<Option<(Uuid, u64)>, Error> {
        let row: Option<(String, i64)> =
            sqlx::query_as("SELECT ledger_id,epoch FROM repair_lineage WHERE slot=1")
                .fetch_optional(&self.db)
                .await?;
        row.map(|(id, epoch)| {
            let id = Uuid::parse_str(&id)
                .map_err(|_| Error::Invalid("invalid native cache repair identity"))?;
            if id.is_nil() || epoch < 0 {
                return Err(Error::Invalid("invalid native cache repair lineage"));
            }
            Ok((id, epoch.cast_unsigned()))
        })
        .transpose()
    }

    /// Witnesses repair state before its new revisions become readable. Refuses
    /// replacement and rollback even when cache payload retention was disabled.
    pub async fn bind_repair_lineage(&self, id: Uuid, epoch: u64) -> Result<(), Error> {
        let epoch = i64::try_from(epoch).map_err(|_| Error::Invalid("invalid repair epoch"))?;
        if id.is_nil() {
            return Err(Error::Invalid("invalid repair ledger identity"));
        }
        let changed = sqlx::query("INSERT INTO repair_lineage(slot,ledger_id,epoch) VALUES(1,$1,$2) ON CONFLICT(slot) DO UPDATE SET epoch=excluded.epoch WHERE repair_lineage.ledger_id=excluded.ledger_id AND repair_lineage.epoch<=excluded.epoch")
            .bind(id.to_string()).bind(epoch).execute(&self.db).await?.rows_affected();
        if changed != 1 {
            return Err(Error::Invalid(
                "native cache belongs to a different or newer repair ledger; restore matching metadata",
            ));
        }
        Ok(())
    }

    pub fn lease(&self, key: Key) -> Option<Arc<Lease>> {
        if (key.size < u64::from(self.settings.min_file_mb) * MB && self.legacy.get().is_none())
            || key.size > i64::MAX.cast_unsigned()
        {
            return None;
        }
        let mut live = self.live.lock().unwrap_or_else(PoisonError::into_inner);
        if live.evicting.contains(&key) || live.legacy_evicting.contains(&key.item()) {
            return None;
        }
        *live.readers.entry(key.clone()).or_default() += 1;
        drop(live);
        Some(Arc::new(Lease {
            key,
            live: Arc::clone(&self.live),
        }))
    }
    fn entry_lock(&self, key: &Key, block: Option<u64>) -> Arc<AsyncMutex<()>> {
        let lock_key = format!("{}:{block:?}", key.id());
        let mut entries = self.entries.lock().unwrap_or_else(PoisonError::into_inner);
        if entries.len() > 4096 {
            entries.retain(|_, v| v.strong_count() > 0);
        }
        if let Some(lock) = entries.get(&lock_key).and_then(Weak::upgrade) {
            return lock;
        }
        let lock = Arc::new(AsyncMutex::new(()));
        entries.insert(lock_key, Arc::downgrade(&lock));
        lock
    }
    pub async fn get(&self, lease: &Lease, block: u64) -> Option<Bytes> {
        let lookup = async {
            if let Some(data) = self.read(lease, block).await? {
                return Ok(Some(data));
            }
            let Some(legacy) = self.legacy.get() else {
                return Ok(None);
            };
            let Ok(permit) = Arc::clone(&self.slots).acquire_owned().await else {
                return Ok(None);
            };
            let bytes = legacy.read(&lease.key, block, permit).await?;
            if bytes.is_some() {
                self.legacy_hits.fetch_add(1, Ordering::Relaxed);
            }
            Ok::<_, Error>(bytes)
        };
        let result = if let Ok(result) = tokio::time::timeout(self.deadline(), lookup).await {
            result
        } else {
            self.read_timeouts.fetch_add(1, Ordering::Relaxed);
            Ok(None)
        };
        match result {
            Ok(Some(data)) => {
                self.hits.fetch_add(1, Ordering::Relaxed);
                Some(data)
            }
            Ok(None) => {
                self.misses.fetch_add(1, Ordering::Relaxed);
                None
            }
            Err(error) => {
                self.failed.fetch_add(1, Ordering::Relaxed);
                tracing::debug!(reason=%error,"native cache read bypassed");
                None
            }
        }
    }
    /// Install once, before admitting playback. The application owns source-provenance checks.
    pub fn set_legacy_reader(&self, reader: Arc<dyn legacy::Reader>) -> Result<(), Error> {
        self.legacy
            .set(reader)
            .map_err(|_| Error::Invalid("legacy cache reader is already installed"))
    }
    /// Warming must not fetch misses when no destination can retain the result.
    pub fn can_fill(&self, key: &Key) -> bool {
        !self.settings.recovery_mode
            && key.size >= u64::from(self.settings.min_file_mb) * MB
            && (self.folders.iter().any(|f| !f.settings.read_only)
                || self.legacy.get().is_some_and(|r| r.writable()))
    }
    async fn read(&self, lease: &Lease, block: u64) -> Result<Option<Bytes>, Error> {
        let key = &lease.key;
        let Some(start) = block.checked_mul(BLOCK).filter(|at| *at < key.size) else {
            return Ok(None);
        };
        let Some(io_lease) = self.lease(key.clone()) else {
            return Ok(None);
        };
        let row:Option<(String,i64,i64,String)>=sqlx::query_as("SELECT e.folder,e.chunk,b.len,b.hash FROM blocks b JOIN entries e ON e.key=b.key WHERE b.key=$1 AND b.block=$2 AND b.valid=1")
            .bind(key.id()).bind(i64::try_from(block).map_err(|_|Error::Invalid("block is too large"))?).fetch_optional(&self.db).await?;
        let Some((folder, chunk, len, hash)) = row else {
            return Ok(None);
        };
        if len <= 0 || len.cast_unsigned() != BLOCK.min(key.size - start) || chunk <= 0 {
            return Ok(None);
        }
        let Some(folder) = self
            .folders
            .iter()
            .find(|f| f.settings.id.to_string() == folder)
            .cloned()
        else {
            return Ok(None);
        };
        let Ok(permit) = Arc::clone(&self.slots).acquire_owned().await else {
            return Ok(None);
        };
        let name = format!("{}-{}.data", key.id(), start / chunk.cast_unsigned());
        let mut observation = self.io.begin(traffic::Kind::Read);
        let job = tokio::task::spawn_blocking(move || {
            observation.start();
            let (_permit, _lease) = (permit, io_lease);
            let file = folder.file(&name, false)?;
            let mut data = vec![
                0;
                usize::try_from(len)
                    .map_err(|_| io::Error::other("invalid block length"))?
            ];
            file.read_exact_at(&mut data, start % chunk.cast_unsigned())?;
            if hex(&Sha256::digest(&data)) != hash {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "native cache block checksum differs",
                ));
            }
            observation.success(data.len() as u64);
            Ok::<_, io::Error>(Bytes::from(data))
        });
        match tokio::time::timeout(self.deadline(), job).await {
            Ok(Ok(Ok(data))) => {
                self.recency.touch(key.id(), now());
                Ok(Some(data))
            }
            Ok(Ok(Err(error))) => {
                self.failed.fetch_add(1, Ordering::Relaxed);
                if matches!(
                    error.kind(),
                    io::ErrorKind::InvalidData
                        | io::ErrorKind::UnexpectedEof
                        | io::ErrorKind::NotFound
                ) {
                    sqlx::query("UPDATE blocks SET valid=0 WHERE key=$1 AND block=$2")
                        .bind(key.id())
                        .bind(block.cast_signed())
                        .execute(&self.db)
                        .await?;
                }
                Ok(None)
            }
            Err(_) => {
                self.read_timeouts.fetch_add(1, Ordering::Relaxed);
                self.failed.fetch_add(1, Ordering::Relaxed);
                Ok(None)
            }
            Ok(Err(_)) => {
                self.failed.fetch_add(1, Ordering::Relaxed);
                Ok(None)
            }
        }
    }
    fn deadline(&self) -> Duration {
        Duration::from_millis(u64::from(self.settings.io_timeout_ms))
    }
    /// Publish only a complete, good block. A saturated writer declines the fill.
    pub fn try_buffer(&self) -> Option<OwnedSemaphorePermit> {
        // One slot always remains for cached playback, even when fills are saturated.
        let mut reservation = Arc::clone(&self.slots).try_acquire_many_owned(2).ok()?;
        reservation.split(1)
    }
    pub async fn put(&self, lease: &Lease, block: u64, data: Bytes) -> Result<bool, Error> {
        let Some(permit) = self.try_buffer() else {
            return Ok(false);
        };
        self.put_reserved(lease, block, data, permit).await
    }
    /// Retains the caller's buffer allowance until disk I/O actually finishes.
    pub async fn put_reserved(
        &self,
        lease: &Lease,
        block: u64,
        data: Bytes,
        permit: OwnedSemaphorePermit,
    ) -> Result<bool, Error> {
        if self.settings.recovery_mode {
            return Ok(false);
        }
        let key = &lease.key;
        if key.size < u64::from(self.settings.min_file_mb) * MB {
            return Ok(false);
        }
        let Some(start) = block.checked_mul(BLOCK).filter(|at| *at < key.size) else {
            return Ok(false);
        };
        if data.len() as u64 != BLOCK.min(key.size - start) {
            return Ok(false);
        }
        if let Some(legacy) = self.legacy.get().filter(|r| r.writable()) {
            return if let Ok(result) =
                tokio::time::timeout(self.deadline(), legacy.write(key, block, data, permit)).await
            {
                result
            } else {
                self.failed.fetch_add(1, Ordering::Relaxed);
                Ok(false)
            };
        }
        let Ok(guard) = self.entry_lock(key, Some(block)).try_lock_owned() else {
            self.skipped.fetch_add(1, Ordering::Relaxed);
            return Ok(false);
        };
        let existing: Option<(String, i64)> =
            sqlx::query_as("SELECT folder,chunk FROM entries WHERE key=$1")
                .bind(key.id())
                .fetch_optional(&self.db)
                .await?;
        let chunk = existing.as_ref().map_or_else(
            || (u64::from(self.settings.chunk_mb) * MB).next_multiple_of(BLOCK),
            |(_, c)| c.cast_unsigned(),
        );
        let found: Option<i64> =
            sqlx::query_scalar("SELECT block FROM blocks WHERE key=$1 AND block=$2 AND valid=1")
                .bind(key.id())
                .bind(i64::try_from(block).map_err(|_| Error::Invalid("invalid block"))?)
                .fetch_optional(&self.db)
                .await?;
        if found.is_some() {
            return Ok(true);
        }
        let reserved: Option<i64> =
            sqlx::query_scalar("SELECT len FROM blocks WHERE key=$1 AND block=$2")
                .bind(key.id())
                .bind(block.cast_signed())
                .fetch_optional(&self.db)
                .await?;
        let additional = (data.len() as u64).saturating_sub(reserved.unwrap_or(0).cast_unsigned());
        for folder in self.folders.iter().filter(|f| {
            !f.settings.read_only
                && existing
                    .as_ref()
                    .is_none_or(|(id, _)| *id == f.settings.id.to_string())
        }) {
            let folder_guard = Arc::clone(&folder.writes).lock_owned().await;
            let used:i64=sqlx::query_scalar("SELECT COALESCE(SUM(b.len),0) FROM blocks b JOIN entries e ON e.key=b.key WHERE e.folder=$1")
                .bind(folder.settings.id.to_string()).fetch_one(&self.db).await?;
            let free = nix::sys::statvfs::fstatvfs(&folder.root)
                .ok()
                .map(|s| crate::filesystem::bytes(s.blocks_available(), s.fragment_size()));
            let high = folder.settings.quota_bytes / 100 * u64::from(folder.settings.high_percent);
            if used.cast_unsigned().saturating_add(additional) > high
                || free.is_none_or(|free| {
                    free < folder
                        .settings
                        .min_free_bytes
                        .saturating_add(data.len() as u64)
                })
            {
                folder
                    .pressure
                    .fetch_max(data.len() as u64, Ordering::Relaxed);
                self.maintenance.notify_one();
                continue;
            }
            let hash = hex(&Sha256::digest(&data));
            let len = i64::try_from(data.len())
                .map_err(|_| Error::Invalid("cache block is too large"))?;
            let folder_id = folder.settings.id.to_string();
            let mut reserve = self.db.begin().await?;
            sqlx::query("INSERT INTO entries(key,item,revision,size,folder,chunk,used) VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(key) DO NOTHING")
                .bind(key.id()).bind(key.item.to_string()).bind(&key.revision).bind(key.size.cast_signed()).bind(&folder_id).bind(chunk.cast_signed()).bind(now()).execute(&mut *reserve).await?;
            let placement: (String, i64) =
                sqlx::query_as("SELECT folder,chunk FROM entries WHERE key=$1")
                    .bind(key.id())
                    .fetch_one(&mut *reserve)
                    .await?;
            if placement.0 != folder_id || placement.1.cast_unsigned() != chunk {
                reserve.rollback().await?;
                continue;
            }
            sqlx::query("INSERT INTO blocks(key,block,len,hash,valid) VALUES($1,$2,$3,$4,0) ON CONFLICT(key,block) DO UPDATE SET len=excluded.len,hash=excluded.hash,valid=0")
                .bind(key.id()).bind(block.cast_signed()).bind(len).bind(&hash).execute(&mut *reserve).await?;
            reserve.commit().await?;
            drop(folder_guard);
            let Some(io_lease) = self.lease(key.clone()) else {
                return Ok(false);
            };
            let name = format!("{}-{}.data", key.id(), start / chunk);
            let folder = Arc::clone(folder);
            let recovery_record =
                manifest::Record::new(&folder, key, chunk, block, data.len() as u64, hash.clone());
            let mut observation = self.io.begin(traffic::Kind::Write);
            let writing = tokio::task::spawn_blocking(move || {
                observation.start();
                let (_permit, _lease) = (permit, io_lease);
                let file = folder.file(&name, true)?;
                file.write_all_at(&data, start % chunk)?;
                file.sync_all()?;
                folder.root.sync_all()?;
                observation.synced_write(data.len() as u64);
                manifest::append(&folder, &recovery_record)?;
                observation.success(0);
                Ok::<_, io::Error>(guard)
            });
            let Ok(Ok(Ok(_guard))) = tokio::time::timeout(self.deadline(), writing).await else {
                self.failed.fetch_add(1, Ordering::Relaxed);
                return Ok(false);
            };
            let mut tx = self.db.begin().await?;
            sqlx::query("INSERT INTO entries(key,item,revision,size,folder,chunk,used) VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(key) DO UPDATE SET used=excluded.used")
                .bind(key.id()).bind(key.item.to_string()).bind(&key.revision).bind(key.size.cast_signed()).bind(folder_id).bind(chunk.cast_signed()).bind(now()).execute(&mut *tx).await?;
            sqlx::query(
                "INSERT INTO blocks(key,block,len,hash) VALUES($1,$2,$3,$4) ON CONFLICT(key,block) DO UPDATE SET len=excluded.len,hash=excluded.hash,valid=1",
            )
            .bind(key.id())
            .bind(block.cast_signed())
            .bind(len)
            .bind(hash)
            .execute(&mut *tx)
            .await?;
            tx.commit().await?;
            return Ok(true);
        }
        self.skipped.fetch_add(1, Ordering::Relaxed);
        Ok(false)
    }
    pub async fn maintenance_requested(&self) {
        self.maintenance.notified().await;
    }
    /// Bounded whole-entry LRU/age eviction. Active leases and pins are always checked.
    pub async fn maintain(self: &Arc<Self>) -> Result<(), Error> {
        type Row = (String, String, i64, i64, i64);
        if self.settings.recovery_mode {
            return Ok(());
        }
        for folder in self.folders.iter().filter(|f| !f.settings.read_only) {
            let mut used:i64=sqlx::query_scalar("SELECT COALESCE(SUM(b.len),0) FROM blocks b JOIN entries e ON e.key=b.key WHERE e.folder=$1").bind(folder.settings.id.to_string()).fetch_one(&self.db).await?;
            let high = folder.settings.quota_bytes / 100 * u64::from(folder.settings.high_percent);
            let wanted = folder.pressure.swap(0, Ordering::Relaxed);
            let target = if wanted > 0 {
                used.cast_unsigned()
                    .saturating_sub(wanted)
                    .min(folder.settings.quota_bytes / 100 * u64::from(folder.settings.low_percent))
            } else if used.cast_unsigned() >= high {
                folder.settings.quota_bytes / 100 * u64::from(folder.settings.low_percent)
            } else {
                used.cast_unsigned()
            };
            let candidates:Vec<Row>=sqlx::query_as("SELECT e.item,e.revision,e.size,e.used,COALESCE(SUM(b.len),0) FROM entries e LEFT JOIN blocks b ON e.key=b.key WHERE e.folder=$1 AND e.pinned=0 GROUP BY e.key ORDER BY e.used LIMIT 100")
                .bind(folder.settings.id.to_string()).fetch_all(&self.db).await?;
            let cutoff = now().saturating_sub(i64::from(folder.settings.max_age_hours) * 3_600_000);
            for (item, revision, size, used_at, bytes) in candidates {
                let aged = folder.settings.max_age_hours > 0 && used_at < cutoff;
                if !aged && used.cast_unsigned() <= target {
                    continue;
                }
                let key = Key {
                    item: Uuid::parse_str(&item)
                        .map_err(|_| Error::Invalid("invalid cached item id"))?,
                    revision,
                    size: size.cast_unsigned(),
                };
                if self
                    .evict(&key, if aged { "age" } else { "pressure" })
                    .await?
                {
                    used = used.saturating_sub(bytes);
                }
            }
        }
        if let Some(legacy) = self.legacy.get() {
            legacy
                .maintain(legacy::Maintenance {
                    cache: self.clone(),
                })
                .await?;
        }
        Ok(())
    }

    pub async fn flush_legacy_recency(&self) -> Result<(), Error> {
        if let Some(legacy) = self.legacy.get() {
            legacy.flush_recency().await?;
        }
        Ok(())
    }

    pub fn legacy_reader(&self) -> Option<&dyn legacy::Reader> {
        self.legacy.get().map(std::convert::AsRef::as_ref)
    }

    pub fn legacy_maintenance(self: &Arc<Self>) -> legacy::Maintenance {
        legacy::Maintenance {
            cache: self.clone(),
        }
    }

    /// Resolve action inputs through the catalogue; clients cannot change lease identity.
    pub async fn resolve(&self, entry: &Entry) -> Result<Option<Key>, Error> {
        let mut key = Key::from_entry(entry)?;
        let size: Option<i64> = sqlx::query_scalar("SELECT size FROM entries WHERE key=$1")
            .bind(key.id())
            .fetch_optional(&self.db)
            .await?;
        Ok(size.map(|size| {
            key.size = size.cast_unsigned();
            key
        }))
    }

    /// Full committed coverage is checked from disk before destructive recovery. A
    /// storage timeout is uncertainty, never evidence that the copy is absent.
    pub async fn verify_complete(&self, key: &Key) -> Result<Option<bool>, Error> {
        let full = |ranges: &[Coverage]| {
            ranges.len() == 1 && ranges[0].start == 0 && ranges[0].end == key.size
        };
        if key.size == 0 || !full(&self.coverage(key).await?) {
            return Ok(if self.settings.recovery_mode {
                None
            } else {
                Some(false)
            });
        }
        let Some(lease) = self.lease(key.clone()) else {
            return Ok(None);
        };
        for block in 0..key.size.div_ceil(BLOCK) {
            if self.get(&lease, block).await.is_none() {
                return Ok(
                    if self.settings.recovery_mode || full(&self.coverage(key).await?) {
                        None
                    } else {
                        Some(false)
                    },
                );
            }
        }
        Ok(Some(true))
    }

    /// Retain a complete catalogue copy before probing storage. Uncertain I/O
    /// keeps protection; conclusive incomplete/corrupt coverage releases only the
    /// automatic protection. User pins remain independent.
    pub async fn verify_and_protect(&self, key: &Key) -> Result<Option<bool>, Error> {
        let Some(_lease) = self.lease(key.clone()) else {
            return Ok(None);
        };
        let full = |ranges: &[Coverage]| {
            ranges.len() == 1 && ranges[0].start == 0 && ranges[0].end == key.size
        };
        if key.size == 0 || !full(&self.coverage(key).await?) {
            if self.settings.recovery_mode {
                return Ok(None);
            }
            self.release_protection(key).await?;
            return Ok(Some(false));
        }
        let native_contributes = !self.native_coverage(key).await?.is_empty();
        let legacy_contributes = if let Some(legacy) = self.legacy.get() {
            !legacy.coverage(key).await?.is_empty()
        } else {
            false
        };
        let protected = sqlx::query(
            "UPDATE entries SET pinned=CASE WHEN pinned=0 THEN 2 ELSE pinned END WHERE key=$1",
        )
        .bind(key.id())
        .execute(&self.db)
        .await?
        .rows_affected()
            > 0;
        if native_contributes && !protected {
            return Ok(None);
        }
        if legacy_contributes
            && let Some(legacy) = self.legacy.get()
            && !legacy.protect(key).await?
        {
            return Ok(None);
        }
        let result = self.verify_complete(key).await?;
        if result == Some(false) {
            self.release_protection(key).await?;
        }
        Ok(result)
    }
    /// Release stale health protection after source removal or revision change.
    /// A manual pin is never cleared by this maintenance operation.
    pub async fn release_protection(&self, key: &Key) -> Result<bool, Error> {
        let native = sqlx::query("UPDATE entries SET pinned=0 WHERE key=$1 AND pinned=2")
            .bind(key.id())
            .execute(&self.db)
            .await?
            .rows_affected()
            > 0;
        let legacy = if let Some(legacy) = self.legacy.get() {
            legacy.release(key).await?
        } else {
            false
        };
        Ok(native || legacy)
    }

    pub async fn coverage(&self, key: &Key) -> Result<Vec<Coverage>, Error> {
        let mut ranges = self.native_coverage(key).await?;
        if let Some(legacy) = self.legacy.get() {
            ranges.extend(legacy.coverage(key).await?);
        }
        ranges.retain(|r| r.start < r.end && r.end <= key.size);
        ranges.sort_by_key(|r| r.start);
        let mut merged: Vec<Coverage> = Vec::new();
        for range in ranges {
            if let Some(last) = merged.last_mut().filter(|r| r.end >= range.start) {
                last.end = last.end.max(range.end);
            } else {
                merged.push(range);
            }
        }
        Ok(merged)
    }
    async fn native_coverage(&self, key: &Key) -> Result<Vec<Coverage>, Error> {
        let blocks: Vec<(i64, i64)> =
            sqlx::query_as("SELECT block,len FROM blocks WHERE key=$1 AND valid=1 ORDER BY block")
                .bind(key.id())
                .fetch_all(&self.db)
                .await?;
        let mut ranges: Vec<Coverage> = Vec::new();
        for (block, len) in blocks {
            let start = block.cast_unsigned() * BLOCK;
            let end = start + len.cast_unsigned();
            if let Some(last) = ranges.last_mut().filter(|last| last.end == start) {
                last.end = end;
            } else {
                ranges.push(Coverage { start, end });
            }
        }
        Ok(ranges)
    }
    pub async fn entries(&self, offset: u32, limit: u32) -> Result<Vec<Entry>, Error> {
        type Row = (String, String, i64, i64, String, i64, i64);
        let rows:Vec<Row>=sqlx::query_as("SELECT e.item,e.revision,e.size,COALESCE(SUM(CASE WHEN b.valid=1 THEN b.len ELSE 0 END),0),e.folder,e.used,e.pinned FROM entries e LEFT JOIN blocks b ON b.key=e.key GROUP BY e.key ORDER BY e.used DESC,e.key LIMIT $1 OFFSET $2")
            .bind(limit.min(200)).bind(offset).fetch_all(&self.db).await?;
        rows.into_iter()
            .map(|(item, revision, size, cached, folder, used, pinned)| {
                Ok(Entry {
                    item: Uuid::parse_str(&item)
                        .map_err(|_| Error::Invalid("invalid catalogue item id"))?,
                    revision,
                    size_bytes: size.cast_unsigned(),
                    cached_bytes: cached.cast_unsigned(),
                    folder_id: Uuid::parse_str(&folder)
                        .map_err(|_| Error::Invalid("invalid catalogue folder id"))?,
                    last_used_ms: used,
                    pinned: pinned != 0,
                })
            })
            .collect()
    }
    pub async fn stats(&self) -> Result<Stats, Error> {
        let files: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM entries")
            .fetch_one(&self.db)
            .await?;
        let bytes: i64 =
            sqlx::query_scalar("SELECT COALESCE(SUM(len),0) FROM blocks WHERE valid=1")
                .fetch_one(&self.db)
                .await?;
        Ok(Stats {
            files: files.cast_unsigned(),
            bytes: bytes.cast_unsigned(),
            hits: self.hits.load(Ordering::Relaxed),
            legacy_hits: self.legacy_hits.load(Ordering::Relaxed),
            legacy_written_bytes: self.legacy.get().map_or(0, |r| r.written_bytes()),
            misses: self.misses.load(Ordering::Relaxed),
            skipped: self.skipped.load(Ordering::Relaxed),
            failed: self.failed.load(Ordering::Relaxed),
            io: self.io.snapshot(self.slots.available_permits()),
            read_timeouts: self.read_timeouts.load(Ordering::Relaxed),
            recency_pending: self.recency.pending(),
            recency_dropped: self.recency.dropped(),
        })
    }
    pub async fn pin(&self, key: &Key, pinned: bool) -> Result<bool, Error> {
        // The lease prevents eviction from starting between admission and the
        // durable pin update, including while SQLite is busy.
        let Some(_lease) = self.lease(key.clone()) else {
            return Ok(false);
        };

        Ok(sqlx::query("UPDATE entries SET pinned=$2 WHERE key=$1")
            .bind(key.id())
            .bind(i64::from(pinned))
            .execute(&self.db)
            .await?
            .rows_affected()
            > 0)
    }
    /// Explicit eviction cannot remove an active or pinned generation.
    pub async fn evict(&self, key: &Key, reason: &str) -> Result<bool, Error> {
        if self.settings.recovery_mode {
            return Ok(false);
        }
        {
            let mut live = self.live.lock().unwrap_or_else(PoisonError::into_inner);
            if live.readers.contains_key(key) || !live.evicting.insert(key.clone()) {
                return Ok(false);
            }
        }
        let mark = EvictionMark {
            key: key.clone(),
            live: Arc::clone(&self.live),
        };
        self.remove(key, reason, mark).await
    }
    async fn remove(&self, key: &Key, reason: &str, mark: EvictionMark) -> Result<bool, Error> {
        let Some(guard) = self.entry_lock(key, None).try_lock_owned().ok() else {
            return Ok(false);
        };
        let row: Option<(String, i64, i64)> =
            sqlx::query_as("SELECT folder,chunk,pinned FROM entries WHERE key=$1")
                .bind(key.id())
                .fetch_optional(&self.db)
                .await?;
        let Some((folder, chunk, 0)) = row else {
            return Ok(false);
        };
        let Some(folder) = self
            .folders
            .iter()
            .find(|f| f.settings.id.to_string() == folder && !f.settings.read_only)
            .cloned()
        else {
            return Ok(false);
        };
        let chunks: Vec<i64> =
            sqlx::query_scalar("SELECT DISTINCT (block * 4194304 / $2) FROM blocks WHERE key=$1")
                .bind(key.id())
                .bind(chunk)
                .fetch_all(&self.db)
                .await?;
        let bytes: i64 = sqlx::query_scalar("SELECT COALESCE(SUM(len),0) FROM blocks WHERE key=$1")
            .bind(key.id())
            .fetch_one(&self.db)
            .await?;
        let Some(permit) = self.try_buffer() else {
            return Ok(false);
        };
        let name_prefix = key.id();
        let mut observation = self.io.begin(traffic::Kind::Remove);
        let work = tokio::task::spawn_blocking(move || {
            observation.start();
            let _permit = permit;
            folder.validate()?;
            for chunk in chunks {
                let name = format!("{name_prefix}-{chunk}.data");
                match nix::unistd::unlinkat(
                    &folder.root,
                    name.as_str(),
                    nix::unistd::UnlinkatFlags::NoRemoveDir,
                ) {
                    Ok(()) | Err(nix::errno::Errno::ENOENT) => {}
                    Err(error) => return Err(io::Error::from(error)),
                }
            }
            let manifest_name = format!("{name_prefix}{}", manifest::SUFFIX);
            match nix::unistd::unlinkat(
                &folder.root,
                manifest_name.as_str(),
                nix::unistd::UnlinkatFlags::NoRemoveDir,
            ) {
                Ok(()) | Err(nix::errno::Errno::ENOENT) => {}
                Err(error) => return Err(io::Error::from(error)),
            }
            folder.root.sync_all()?;
            observation.success(0);
            Ok::<_, io::Error>((guard, mark))
        });
        // A timed-out/cancelled delete still holds its generation/entry guards until
        // real I/O ends. It cannot unlink files from a new writer of the same key.
        let Ok(Ok(Ok((_guard, _mark)))) = tokio::time::timeout(self.deadline(), work).await else {
            self.failed.fetch_add(1, Ordering::Relaxed);
            return Ok(false);
        };
        let mut tx = self.db.begin().await?;
        sqlx::query("DELETE FROM entries WHERE key=$1")
            .bind(key.id())
            .execute(&mut *tx)
            .await?;
        sqlx::query("INSERT INTO evictions(item,revision,bytes,at,reason) VALUES($1,$2,$3,$4,$5)")
            .bind(key.item.to_string())
            .bind(&key.revision)
            .bind(bytes)
            .bind(now())
            .bind(reason)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM evictions WHERE rowid NOT IN(SELECT rowid FROM evictions ORDER BY at DESC,rowid DESC LIMIT 10000)").execute(&mut *tx).await?;
        tx.commit().await?;
        Ok(true)
    }
}
fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(i64::MAX)
}

fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    bytes
        .iter()
        .flat_map(|&b| {
            [
                char::from(DIGITS[usize::from(b >> 4)]),
                char::from(DIGITS[usize::from(b & 15)]),
            ]
        })
        .collect()
}

struct EvictionMark {
    key: Key,
    live: Arc<Mutex<Live>>,
}
impl Drop for EvictionMark {
    fn drop(&mut self) {
        self.live
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .evicting
            .remove(&self.key);
    }
}
