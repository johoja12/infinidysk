//! Durable article repairs share the local native-cache catalogue. Payload eviction
//! keeps the content revision and proof. Catalogue rollback below the fsynced anchor
//! fails closed instead of reverting repaired articles to an unversioned source.
use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::future::Future;
use std::io::{Read as _, Write as _};
use std::os::unix::fs::{DirBuilderExt as _, MetadataExt as _, OpenOptionsExt as _};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, OnceLock, PoisonError, RwLock};
use std::time::Duration;

use bytes::Bytes;
use nix::fcntl::{Flock, FlockArg, OFlag, openat, renameat};
use nix::sys::stat::Mode;
use nix::unistd::{UnlinkatFlags, unlinkat};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use sqlx::SqlitePool;
use sqlx::sqlite::{SqliteConnectOptions, SqliteJournalMode, SqlitePoolOptions, SqliteSynchronous};
use tokio::sync::Mutex;
use uuid::Uuid;

const ANCHOR: &str = "repair-anchor.json";
const MAX_ARTICLE: usize = 32 << 20;
const MAX_BATCH: usize = 256 << 20;
const MAX_ENTRIES: usize = 100_000;
const MAX_PAYLOAD: u64 = 4 << 30;
const MAX_ID_BYTES: u64 = 16 << 20;
const MAX_RECORD_BYTES: u64 = 64 << 20;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("repair ledger I/O failed")]
    Io(#[from] std::io::Error),
    #[error("repair ledger filesystem operation failed")]
    System(#[from] nix::errno::Errno),
    #[error("repair ledger storage failed")]
    Db(#[from] sqlx::Error),
    #[error("repair ledger anchor is unreadable")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Invalid(&'static str),
    #[error("repair publication witness failed; readback is required")]
    Witness,
    #[error("repair publication requires reconciliation before another attempt")]
    Pending,
    #[error("article repair revision changed during the read")]
    Changed,
    #[error("verified repair payload is unavailable; its revision is retained")]
    PayloadMissing,
    #[error("repair ledger exceeds its payload, batch or record allowance")]
    Budget,
}
pub use infinidysk_nntp::RepairRevision as Revision;
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Geometry {
    pub offset: u64,
    pub file_size: u64,
    pub part: Option<u32>,
    pub total: Option<u32>,
}
/// Exact source proof supplied by the whole-file verification/publishing caller.
/// This storage layer does not discover sources or perform PAR2 reconstruction.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Proof {
    pub set: [u8; 16],
    pub file: [u8; 16],
    pub file_md5: [u8; 16],
    pub file_length: u64,
}
pub struct Patch {
    pub message_id: String,
    pub geometry: Geometry,
    pub proof: Proof,
    pub data: Bytes,
}
pub struct Article {
    pub geometry: Geometry,
    pub proof: Proof,
    pub data: Bytes,
}
#[derive(Clone, Copy)]
struct Info {
    revision: [u8; 32],
    hash: [u8; 32],
    geometry: Geometry,
    proof: Proof,
    length: usize,
    present: bool,
}
#[derive(Clone, Copy)]
enum Entry {
    Pending,
    Verified(Info),
}
#[derive(Serialize, Deserialize)]
struct Anchor {
    version: u8,
    id: Uuid,
    epoch: u64,
}
struct Root {
    path: PathBuf,
    dir: File,
    _lock: Flock<File>,
    device: u64,
    inode: u64,
}
impl Root {
    fn validate(&self) -> Result<(), Error> {
        let meta = std::fs::symlink_metadata(&self.path)?;
        if !meta.is_dir() || meta.dev() != self.device || meta.ino() != self.inode {
            return Err(Error::Invalid("repair ledger directory identity changed"));
        }
        Ok(())
    }
    fn anchor(&self) -> Result<Option<Anchor>, Error> {
        self.validate()?;
        let file = match openat(
            &self.dir,
            ANCHOR,
            OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
            Mode::empty(),
        ) {
            Ok(fd) => File::from(fd),
            Err(nix::errno::Errno::ENOENT) => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        if !file.metadata()?.is_file()
            || file.metadata()?.nlink() != 1
            || file.metadata()?.len() > 1024
        {
            return Err(Error::Invalid(
                "repair ledger anchor is not a private regular file",
            ));
        }
        let mut bytes = Vec::new();
        file.take(1025).read_to_end(&mut bytes)?;
        Ok(Some(serde_json::from_slice(&bytes)?))
    }
    fn write_anchor(&self, id: Uuid, epoch: u64) -> Result<(), Error> {
        self.validate()?;
        let name = format!(".repair-anchor-{}", Uuid::new_v4());
        let mut file = File::from(openat(
            &self.dir,
            name.as_str(),
            OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW,
            Mode::S_IRUSR | Mode::S_IWUSR,
        )?);
        let result = (|| {
            file.write_all(&serde_json::to_vec(&Anchor {
                version: 1,
                id,
                epoch,
            })?)?;
            file.sync_all()?;
            renameat(&self.dir, name.as_str(), &self.dir, ANCHOR)?;
            self.dir.sync_all()?;
            Ok(())
        })();
        if result.is_err() {
            let _ = unlinkat(&self.dir, name.as_str(), UnlinkatFlags::NoRemoveDir);
        }
        result
    }
}

/// Persists main-database/external-cache lineage before index activation. It must
/// never call back into this ledger's publication/reconciliation methods.
pub type Witness =
    Arc<dyn Fn(Uuid, u64) -> Pin<Box<dyn Future<Output = Result<(), Error>> + Send>> + Send + Sync>;

pub struct Store {
    db: SqlitePool,
    root: Arc<Root>,
    id: Uuid,
    epoch: AtomicU64,
    generation: AtomicU64,
    pending: AtomicBool,
    index: RwLock<BTreeMap<String, Entry>>,
    writer: Arc<Mutex<()>>,
    witness: OnceLock<Witness>,
    publication_gate: OnceLock<Arc<tokio::sync::RwLock<()>>>,
}
impl Store {
    /// Opens the same local metadata directory used by the final-media cache. This
    /// ledger is independent of whether final-media payload retention is enabled.
    pub async fn open(path: &Path) -> Result<Arc<Self>, Error> {
        if !path.is_absolute() || path.parent().is_none() {
            return Err(Error::Invalid(
                "repair ledger needs an absolute non-root directory",
            ));
        }
        std::fs::DirBuilder::new()
            .recursive(true)
            .mode(0o700)
            .create(path)?;
        let path = std::fs::canonicalize(path)?;
        let kind = nix::sys::statfs::statfs(&path)?.filesystem_type().0;
        if matches!(kind, 0x6969 | 0xFF53_4D42 | 0x517B) {
            return Err(Error::Invalid("repair ledger must use local storage"));
        }
        let dir = OpenOptions::new()
            .read(true)
            .custom_flags((OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW).bits())
            .open(&path)?;
        let meta = dir.metadata()?;
        let lock = File::from(openat(
            &dir,
            ".repair-writer.lock",
            OFlag::O_RDWR | OFlag::O_CREAT | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
            Mode::S_IRUSR | Mode::S_IWUSR,
        )?);
        if !lock.metadata()?.is_file() || lock.metadata()?.nlink() != 1 {
            return Err(Error::Invalid("invalid repair writer lock"));
        }
        let lock =
            Flock::lock(lock, FlockArg::LockExclusiveNonblock).map_err(|(_, error)| error)?;
        let root = Arc::new(Root {
            path,
            dir,
            _lock: lock,
            device: meta.dev(),
            inode: meta.ino(),
        });
        let anchor = root.anchor()?;
        let dbpath = root.path.join("catalogue.sqlite");
        match std::fs::symlink_metadata(&dbpath) {
            Ok(m) if m.is_file() && m.nlink() == 1 => {}
            Ok(_) => {
                return Err(Error::Invalid(
                    "repair catalogue is not a regular owned file",
                ));
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound && anchor.is_none() => {}
            Err(error) => return Err(error.into()),
        }
        let db = SqlitePoolOptions::new()
            .max_connections(4)
            .connect_with(
                SqliteConnectOptions::new()
                    .filename(dbpath)
                    .create_if_missing(anchor.is_none())
                    .foreign_keys(true)
                    .journal_mode(SqliteJournalMode::Wal)
                    .synchronous(SqliteSynchronous::Full)
                    .busy_timeout(Duration::from_secs(5)),
            )
            .await?;
        if sqlx::query_scalar::<_, i64>("PRAGMA user_version")
            .fetch_one(&db)
            .await?
            > super::SCHEMA
        {
            return Err(Error::Invalid(
                "native cache catalogue is newer than this binary",
            ));
        }
        for sql in [
            "CREATE TABLE IF NOT EXISTS repair_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL,id TEXT NOT NULL,epoch BIGINT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS article_repairs(message_id TEXT PRIMARY KEY,revision BLOB NOT NULL,hash BLOB NOT NULL,geometry TEXT NOT NULL,proof TEXT NOT NULL,length BIGINT NOT NULL,payload BLOB)",
        ] {
            sqlx::query(sql).execute(&db).await?;
        }
        let saved: Option<(i64, String, i64)> =
            sqlx::query_as("SELECT version,id,epoch FROM repair_state WHERE singleton=1")
                .fetch_optional(&db)
                .await?;
        let (id, epoch) = if let Some((version, id, epoch)) = saved {
            if version != 1 || epoch < 0 {
                return Err(Error::Invalid("unsupported repair ledger version or epoch"));
            }
            (
                Uuid::parse_str(&id)
                    .map_err(|_| Error::Invalid("invalid repair ledger identity"))?,
                epoch.cast_unsigned(),
            )
        } else {
            if anchor.is_some() {
                return Err(Error::Invalid(
                    "repair state is missing; restore the original catalogue",
                ));
            }
            let id = Uuid::new_v4();
            sqlx::query("INSERT INTO repair_state VALUES(1,1,$1,0)")
                .bind(id.to_string())
                .execute(&db)
                .await?;
            (id, 0)
        };
        check_anchor(anchor.as_ref(), id, epoch)?;
        let index = load(&db).await?;
        let stamping = Arc::clone(&root);
        tokio::task::spawn_blocking(move || stamping.write_anchor(id, epoch))
            .await
            .map_err(|_| Error::Invalid("repair anchor worker failed"))??;
        Ok(Arc::new(Self {
            db,
            root,
            id,
            epoch: AtomicU64::new(epoch),
            generation: AtomicU64::new(epoch.max(1)),
            pending: AtomicBool::new(false),
            index: RwLock::new(index),
            writer: Arc::new(Mutex::new(())),
            witness: OnceLock::new(),
            publication_gate: OnceLock::new(),
        }))
    }
    /// Coordinates final publication with automatic replacement. Acquired before
    /// opening a write transaction, so shared catalogue readers cannot deadlock it.
    pub fn set_mutation_gate(&self, gate: Arc<tokio::sync::RwLock<()>>) -> bool {
        self.publication_gate.set(gate).is_ok()
    }
    async fn publication_guard(&self) -> Option<tokio::sync::OwnedRwLockWriteGuard<()>> {
        match self.publication_gate.get() {
            Some(gate) => Some(Arc::clone(gate).write_owned().await),
            None => None,
        }
    }
    /// Install during application bootstrap before attaching the article source.
    pub fn set_witness(&self, witness: Witness) -> bool {
        self.witness.set(witness).is_ok()
    }
    #[must_use]
    pub fn is_pending(&self) -> bool {
        self.pending.load(Ordering::Acquire)
    }
    async fn witness(&self, epoch: u64) -> Result<(), Error> {
        if let Some(witness) = self.witness.get() {
            witness(self.id, epoch).await?;
        }
        Ok(())
    }
    #[must_use]
    pub const fn id(&self) -> Uuid {
        self.id
    }
    #[must_use]
    pub fn epoch(&self) -> u64 {
        self.epoch.load(Ordering::Acquire)
    }
    /// Runtime change counter includes in-progress and uncertain publication.
    #[must_use]
    pub fn generation(&self) -> u64 {
        self.generation.load(Ordering::Acquire)
    }
    #[must_use]
    pub fn revision(&self, id: &str) -> Revision {
        match self
            .index
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(id)
        {
            None => Revision::Base,
            Some(Entry::Pending) => Revision::Pending,
            Some(Entry::Verified(info)) => Revision::Repaired(info.revision),
        }
    }
    fn info(&self, id: &str, revision: [u8; 32]) -> Result<Info, Error> {
        match self
            .index
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(id)
        {
            Some(Entry::Verified(info)) if info.revision == revision => Ok(*info),
            Some(Entry::Pending) => Err(Error::Pending),
            _ => Err(Error::Changed),
        }
    }
    pub async fn get(&self, id: &str, revision: [u8; 32]) -> Result<Article, Error> {
        let info = self.info(id, revision)?;
        if !info.present {
            return Err(Error::PayloadMissing);
        }
        self.root.validate()?;
        let row: Option<(Option<Vec<u8>>,)> = sqlx::query_as(
            "SELECT CASE WHEN LENGTH(payload)=$3 THEN payload ELSE NULL END FROM article_repairs WHERE message_id=$1 AND revision=$2",
        )
        .bind(id)
        .bind(revision.as_slice())
        .bind(i64::try_from(info.length).map_err(|_| Error::Budget)?)
        .fetch_optional(&self.db)
        .await?;
        let data = row.ok_or(Error::Changed)?.0.ok_or(Error::PayloadMissing)?;
        let valid = tokio::task::spawn_blocking(move || {
            if data.len() != info.length || <[u8; 32]>::from(Sha256::digest(&data)) != info.hash {
                return Err(Error::Invalid("repair payload checksum mismatch"));
            }
            Ok(Bytes::from(data))
        })
        .await
        .map_err(|_| Error::Invalid("repair verification worker failed"))??;
        if self.revision(id) != Revision::Repaired(revision) {
            return Err(Error::Changed);
        }
        Ok(Article {
            geometry: info.geometry,
            proof: info.proof,
            data: valid,
        })
    }
    /// Commits only caller-verified complete article patches. Publication is atomic
    /// across the batch. If commit/fsync is uncertain, affected IDs stay Pending
    /// until explicit readback reconciliation; they never fall back to base bytes.
    pub async fn publish(&self, patches: Vec<Patch>) -> Result<(), Error> {
        let writer = Arc::clone(&self.writer).lock_owned().await;
        if self.pending.load(Ordering::Acquire) {
            return Err(Error::Pending);
        }
        self.root.validate()?;
        if patches.is_empty() || patches.len() > 1024 {
            return Err(Error::Budget);
        }
        let (writer, prepared) = tokio::task::spawn_blocking(move || {
            let result = (|| {
                let mut ids = BTreeSet::new();
                let mut batch = 0usize;
                let mut prepared = Vec::with_capacity(patches.len());
                for patch in patches {
                    if !ids.insert(patch.message_id.clone()) {
                        return Err(Error::Invalid("duplicate article in repair publication"));
                    }
                    batch = batch
                        .checked_add(patch.data.len())
                        .filter(|n| *n <= MAX_BATCH)
                        .ok_or(Error::Budget)?;
                    let info = prepare(&patch)?;
                    prepared.push((patch, info));
                }
                Ok(prepared)
            })();
            (writer, result)
        })
        .await
        .map_err(|_| Error::Invalid("repair preparation worker failed"))?;
        let prepared = prepared?;
        let _publication = self.publication_guard().await;
        let mut tx = self.db.begin_with("BEGIN IMMEDIATE").await?;
        let mut bytes: i64 =
            sqlx::query_scalar("SELECT COALESCE(SUM(LENGTH(payload)),0) FROM article_repairs")
                .fetch_one(&mut *tx)
                .await?;
        let mut count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM article_repairs")
            .fetch_one(&mut *tx)
            .await?;
        for (patch, _) in &prepared {
            let existing: Option<(i64,)> = sqlx::query_as(
                "SELECT COALESCE(LENGTH(payload),0) FROM article_repairs WHERE message_id=$1",
            )
            .bind(&patch.message_id)
            .fetch_optional(&mut *tx)
            .await?;
            if let Some((old,)) = existing {
                bytes = bytes.checked_sub(old).ok_or(Error::Budget)?;
            } else {
                count += 1;
            }
            bytes = bytes
                .checked_add(i64::try_from(patch.data.len()).map_err(|_| Error::Budget)?)
                .ok_or(Error::Budget)?;
        }
        if bytes < 0
            || bytes.cast_unsigned() > MAX_PAYLOAD
            || count < 0
            || count.cast_unsigned() > MAX_ENTRIES as u64
        {
            return Err(Error::Budget);
        }
        let (mut id_bytes, mut record_bytes): (i64, i64) = sqlx::query_as("SELECT COALESCE(SUM(LENGTH(CAST(message_id AS BLOB))),0),COALESCE(SUM(LENGTH(geometry)+LENGTH(proof)),0) FROM article_repairs").fetch_one(&mut *tx).await?;
        for (patch, _) in &prepared {
            let old: Option<i64> = sqlx::query_scalar(
                "SELECT LENGTH(geometry)+LENGTH(proof) FROM article_repairs WHERE message_id=$1",
            )
            .bind(&patch.message_id)
            .fetch_optional(&mut *tx)
            .await?;
            if let Some(old) = old {
                record_bytes = record_bytes.checked_sub(old).ok_or(Error::Budget)?;
            } else {
                id_bytes = id_bytes
                    .checked_add(i64::try_from(patch.message_id.len()).map_err(|_| Error::Budget)?)
                    .ok_or(Error::Budget)?;
            }
            let added = serde_json::to_vec(&(patch.geometry))?.len()
                + serde_json::to_vec(&(patch.proof))?.len();
            record_bytes = record_bytes
                .checked_add(i64::try_from(added).map_err(|_| Error::Budget)?)
                .ok_or(Error::Budget)?;
        }
        if id_bytes < 0
            || id_bytes.cast_unsigned() > MAX_ID_BYTES
            || record_bytes < 0
            || record_bytes.cast_unsigned() > MAX_RECORD_BYTES
        {
            return Err(Error::Budget);
        }
        let next = self
            .epoch()
            .checked_add(1)
            .and_then(|n| i64::try_from(n).ok())
            .ok_or(Error::Budget)?;
        // Pending precedes any durable mutation and invalidates old in-flight stamps.
        self.pending.store(true, Ordering::Release);
        {
            let mut index = self.index.write().unwrap_or_else(PoisonError::into_inner);
            for (patch, _) in &prepared {
                index.insert(patch.message_id.clone(), Entry::Pending);
            }
            self.generation.fetch_add(1, Ordering::Release);
            drop(index); // Publish the generation and index under the same guard.
        }
        let updated = sqlx::query("UPDATE repair_state SET epoch=$1 WHERE singleton=1 AND version=1 AND id=$2 AND epoch=$3")
            .bind(next).bind(self.id.to_string()).bind(i64::try_from(self.epoch()).map_err(|_| Error::Budget)?).execute(&mut *tx).await?.rows_affected();
        if updated != 1 {
            return Err(Error::Pending);
        }
        for (patch, info) in &prepared {
            sqlx::query("INSERT INTO article_repairs VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision,hash=excluded.hash,geometry=excluded.geometry,proof=excluded.proof,length=excluded.length,payload=excluded.payload")
                .bind(&patch.message_id).bind(info.revision.as_slice()).bind(info.hash.as_slice()).bind(serde_json::to_string(&patch.geometry)?).bind(serde_json::to_string(&patch.proof)?)
                .bind(i64::try_from(info.length).map_err(|_| Error::Budget)?).bind(patch.data.as_ref()).execute(&mut *tx).await?;
        }
        tx.commit().await?;
        let (root, id) = (Arc::clone(&self.root), self.id);
        let (_writer, anchored) = tokio::task::spawn_blocking(move || {
            (writer, root.write_anchor(id, next.cast_unsigned()))
        })
        .await
        .map_err(|_| Error::Invalid("repair anchor worker failed"))?;
        anchored?;
        self.witness(next.cast_unsigned()).await?;
        {
            let mut index = self.index.write().unwrap_or_else(PoisonError::into_inner);
            for (patch, info) in prepared {
                index.insert(patch.message_id, Entry::Verified(info));
            }
            self.epoch.store(next.cast_unsigned(), Ordering::Release);
            self.generation.fetch_add(1, Ordering::Release);
            drop(index); // Publish the generation and index under the same guard.
        }
        self.pending.store(false, Ordering::Release);
        Ok(())
    }
    /// Reconciles uncertain writes by reading durable state, not replaying patches.
    pub async fn reconcile(&self) -> Result<(), Error> {
        let writer = Arc::clone(&self.writer).lock_owned().await;
        let (version, id, epoch): (i64, String, i64) =
            sqlx::query_as("SELECT version,id,epoch FROM repair_state WHERE singleton=1")
                .fetch_one(&self.db)
                .await?;
        if version != 1
            || id != self.id.to_string()
            || epoch < 0
            || epoch.cast_unsigned() < self.epoch()
        {
            return Err(Error::Invalid("repair ledger identity or epoch regressed"));
        }
        check_anchor(self.root.anchor()?.as_ref(), self.id, epoch.cast_unsigned())?;
        let index = load(&self.db).await?;
        let _publication = self.publication_guard().await;
        let (root, id) = (Arc::clone(&self.root), self.id);
        let (_writer, anchored) = tokio::task::spawn_blocking(move || {
            (writer, root.write_anchor(id, epoch.cast_unsigned()))
        })
        .await
        .map_err(|_| Error::Invalid("repair anchor worker failed"))?;
        anchored?;
        self.witness(epoch.cast_unsigned()).await?;
        {
            let mut current = self.index.write().unwrap_or_else(PoisonError::into_inner);
            *current = index;
            self.epoch.store(epoch.cast_unsigned(), Ordering::Release);
            self.generation.fetch_add(1, Ordering::Release);
            drop(current); // Publish the generation and index under the same guard.
        }
        self.pending.store(false, Ordering::Release);
        Ok(())
    }
    /// Payload retention only: revisions/proofs cannot be removed by this operation.
    pub async fn evict_payload(&self, id: &str) -> Result<bool, Error> {
        let _writer = Arc::clone(&self.writer).lock_owned().await;
        if self.pending.load(Ordering::Acquire) {
            return Err(Error::Pending);
        }
        self.root.validate()?;
        let changed = sqlx::query(
            "UPDATE article_repairs SET payload=NULL WHERE message_id=$1 AND payload IS NOT NULL",
        )
        .bind(id)
        .execute(&self.db)
        .await?
        .rows_affected()
            > 0;
        if let Some(Entry::Verified(info)) = self
            .index
            .write()
            .unwrap_or_else(PoisonError::into_inner)
            .get_mut(id)
        {
            info.present = false;
        }
        Ok(changed)
    }
}
fn check_anchor(anchor: Option<&Anchor>, id: Uuid, epoch: u64) -> Result<(), Error> {
    if anchor.is_some_and(|a| a.version != 1 || a.id != id || a.epoch > epoch) {
        return Err(Error::Invalid(
            "repair catalogue is older than its durable anchor; restore the matching state",
        ));
    }
    Ok(())
}
fn identity(
    id: &str,
    geometry: Geometry,
    proof: Proof,
    length: usize,
    hash: [u8; 32],
) -> Result<[u8; 32], Error> {
    if id.is_empty()
        || id.len() > 4096
        || id
            .bytes()
            .any(|b| b.is_ascii_control() || b.is_ascii_whitespace() || b == b'<' || b == b'>')
        || length == 0
        || length > MAX_ARTICLE
        || geometry.file_size == 0
        || proof.file_length != geometry.file_size
        || geometry
            .offset
            .checked_add(length as u64)
            .is_none_or(|end| end > geometry.file_size)
        || geometry.part == Some(0)
        || geometry.total == Some(0)
        || geometry
            .part
            .zip(geometry.total)
            .is_some_and(|(part, total)| part > total)
    {
        return Err(Error::Invalid("invalid complete article repair"));
    }
    let mut revision = Sha256::new();
    revision.update(b"infinidysk-article-repair-v1");
    revision.update((id.len() as u64).to_le_bytes());
    revision.update(id);
    revision.update(serde_json::to_vec(&(geometry, proof))?);
    revision.update(hash);
    Ok(revision.finalize().into())
}
fn prepare(patch: &Patch) -> Result<Info, Error> {
    let hash: [u8; 32] = Sha256::digest(&patch.data).into();
    Ok(Info {
        revision: identity(
            &patch.message_id,
            patch.geometry,
            patch.proof,
            patch.data.len(),
            hash,
        )?,
        hash,
        geometry: patch.geometry,
        proof: patch.proof,
        length: patch.data.len(),
        present: true,
    })
}
type Row = (String, Vec<u8>, Vec<u8>, String, String, i64, Option<i64>);
async fn load(db: &SqlitePool) -> Result<BTreeMap<String, Entry>, Error> {
    let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM article_repairs")
        .fetch_one(db)
        .await?;
    if count < 0 || count.cast_unsigned() > MAX_ENTRIES as u64 {
        return Err(Error::Budget);
    }
    let (payload_bytes, id_bytes, record_bytes, invalid): (i64, i64, i64, i64) = sqlx::query_as("SELECT COALESCE(SUM(LENGTH(payload)),0),COALESCE(SUM(LENGTH(CAST(message_id AS BLOB))),0),COALESCE(SUM(LENGTH(geometry)+LENGTH(proof)),0),COALESCE(SUM(CASE WHEN LENGTH(CAST(message_id AS BLOB))>4096 OR LENGTH(geometry)>1024 OR LENGTH(proof)>2048 OR LENGTH(revision)!=32 OR LENGTH(hash)!=32 THEN 1 ELSE 0 END),0) FROM article_repairs").fetch_one(db).await?;
    if payload_bytes < 0
        || payload_bytes.cast_unsigned() > MAX_PAYLOAD
        || id_bytes < 0
        || id_bytes.cast_unsigned() > MAX_ID_BYTES
        || record_bytes < 0
        || record_bytes.cast_unsigned() > MAX_RECORD_BYTES
        || invalid != 0
    {
        return Err(Error::Budget);
    }
    let rows: Vec<Row> = sqlx::query_as("SELECT message_id,revision,hash,geometry,proof,length,LENGTH(payload) FROM article_repairs LIMIT 100001").fetch_all(db).await?;
    let mut index = BTreeMap::new();
    for (id, revision, hash, geometry, proof, length, payload) in rows {
        if id.is_empty()
            || id.len() > 4096
            || geometry.len() > 1024
            || proof.len() > 2048
            || length <= 0
            || length.cast_unsigned() > MAX_ARTICLE as u64
            || payload.is_some_and(|n| n != length)
        {
            return Err(Error::Invalid("invalid persisted repair record"));
        }
        let info = Info {
            revision: revision
                .try_into()
                .map_err(|_| Error::Invalid("invalid repair revision"))?,
            hash: hash
                .try_into()
                .map_err(|_| Error::Invalid("invalid repair checksum"))?,
            geometry: serde_json::from_str(&geometry)?,
            proof: serde_json::from_str(&proof)?,
            length: usize::try_from(length).map_err(|_| Error::Budget)?,
            present: payload.is_some(),
        };
        if identity(&id, info.geometry, info.proof, info.length, info.hash)? != info.revision {
            return Err(Error::Invalid(
                "repair record does not match its content revision",
            ));
        }
        index.insert(id, Entry::Verified(info));
    }
    Ok(index)
}

impl infinidysk_nntp::ArticleRepairs for Store {
    fn revision(&self, id: &str) -> Revision {
        Self::revision(self, id)
    }
    fn generation(&self) -> u64 {
        Self::generation(self)
    }
    fn expectation(
        &self,
        id: &str,
        revision: [u8; 32],
    ) -> Result<infinidysk_nntp::BodyExpectation, infinidysk_nntp::Error> {
        let info = self
            .info(id, revision)
            .map_err(|_| infinidysk_nntp::Error::Unavailable)?;
        Ok(infinidysk_nntp::BodyExpectation {
            offset: Some(info.geometry.offset),
            file_size: Some(info.geometry.file_size),
            decoded_len: Some(info.length as u64),
            part: info.geometry.part,
            total: info.geometry.total,
        })
    }
    fn body<'a>(
        &'a self,
        id: &'a str,
        revision: [u8; 32],
    ) -> infinidysk_nntp::ArticleFuture<'a, infinidysk_nntp::Body> {
        Box::pin(async move {
            let article = self
                .get(id, revision)
                .await
                .map_err(|_| infinidysk_nntp::Error::Unavailable)?;
            Ok(infinidysk_nntp::Body {
                data: article.data,
                offset: article.geometry.offset,
                file_size: article.geometry.file_size,
                part: article.geometry.part,
                total: article.geometry.total,
            })
        })
    }
}
