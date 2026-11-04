//! The tree served by `WebDAV` and FUSE alike:
//!
//! - `/content/<category>/<job>/…`: imported files.
//! - `/.ids/a/b/c/d/e/<id>`: every file by id, sharded by the id's first five hex digits.
//! - `/completed-symlinks/<category>/<job>/…`: completed downloads, each file a link to
//!   its `/.ids` path under the mount directory.
//! - `/nzbs/<category>/<file>`: queued NZBs.
//! - `/README`.

mod backfill;
mod native;
mod reader;
mod revision;
mod session;
pub use backfill::{Backfill, Backfills};

use std::collections::{BTreeSet, HashMap};
use std::hash::RandomState;
use std::io::Cursor;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, PoisonError};

use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, blob, media};
use infinidysk_db::{Db, HealthState, Item, QueueEntry};
use infinidysk_nntp::{Client, RepairRevision};
use infinidysk_stream::{Budget, Cache, Fault, Gaps, Report};
use sha2::{Digest as _, Sha256};
use uuid::Uuid;

pub use reader::{PartsReader, Reader};
pub use session::{
    Delivery, Handle, Origin, PendingRead, Playback, ReadStage, RepairGuard, Request, Session,
    Sessions, Tracing,
};

/// What the kernel applied to the FUSE mount, read back after tuning; `None` where
/// unreadable.
#[derive(Clone, Debug, Default)]
pub struct Kernel {
    /// The connection's FUSE control directory.
    pub connection: Option<std::path::PathBuf>,
    pub read_ahead_kb: Option<u64>,
    /// Requests the kernel keeps in flight before it queues the rest.
    pub max_background: Option<u32>,
    pub congestion_threshold: Option<u32>,
}

impl Kernel {
    /// Requests the kernel holds that are not answered yet, queued or in our hands.
    #[must_use]
    pub fn waiting(&self) -> Option<u64> {
        let dir = self.connection.as_ref()?;
        std::fs::read_to_string(dir.join("waiting"))
            .ok()?
            .trim()
            .parse()
            .ok()
    }
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("file repair is in progress; retry shortly")]
    Repairing,
    #[error(transparent)]
    Db(#[from] infinidysk_db::Error),
    #[error("stored blob is unreadable")]
    Blob(#[from] blob::Error),
    #[error("stored data is invalid: {0}")]
    Corrupt(&'static str),
}

/// Hex digits of the id prefix that shard `/.ids`.
const SHARD_DEPTH: usize = 5;
const HEX: &[u8; 16] = b"0123456789abcdef";
const README: &str = "\
content             imported files, by category and download
completed-symlinks  completed downloads; each file links to its .ids path
.ids                every file by id
nzbs                queued NZBs, by category
";

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Node {
    Root,
    Readme,
    /// `/content` or anything under it.
    Item(Item),
    /// `/.ids` (empty prefix) or one of its shard directories.
    Shard(String),
    /// A file under `/.ids`, named by its id.
    IdFile(Item),
    Nzbs,
    NzbCategory(String),
    Nzb(QueueEntry),
    /// `/completed-symlinks` (`None`) or a directory under it mirroring `/content`.
    Links(Option<Item>),
    /// A file under `/completed-symlinks`, pointing at `target`.
    Link {
        item: Item,
        target: String,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Dir,
    File,
    Symlink,
}

impl Node {
    pub fn name(&self) -> String {
        match self {
            Self::Root => String::new(),
            Self::Readme => "README".to_owned(),
            Self::Item(item) | Self::Links(Some(item)) | Self::Link { item, .. } => {
                item.name.clone()
            }
            Self::Shard(prefix) if prefix.is_empty() => ".ids".to_owned(),
            Self::Shard(prefix) => prefix[prefix.len() - 1..].to_owned(),
            Self::IdFile(item) => item.id.to_string(),
            Self::Nzbs => "nzbs".to_owned(),
            Self::NzbCategory(category) => category.clone(),
            Self::Nzb(entry) => entry.file_name.clone(),
            Self::Links(None) => "completed-symlinks".to_owned(),
        }
    }

    pub fn kind(&self) -> Kind {
        match self {
            Self::Item(item) | Self::Links(Some(item)) if item.kind == ItemKind::Directory => {
                Kind::Dir
            }
            Self::Root | Self::Shard(_) | Self::Nzbs | Self::NzbCategory(_) | Self::Links(_) => {
                Kind::Dir
            }
            Self::Link { .. } => Kind::Symlink,
            Self::Readme | Self::Item(_) | Self::IdFile(_) | Self::Nzb(_) => Kind::File,
        }
    }

    /// Bytes a read returns; a link's size is its target's length.
    pub fn size(&self) -> u64 {
        match self {
            Self::Readme => README.len() as u64,
            Self::Item(item) | Self::IdFile(item) => item.size.unwrap_or(0),
            Self::Nzb(entry) => entry.nzb_size,
            Self::Link { target, .. } => target.len() as u64,
            _ => 0,
        }
    }

    /// Unix milliseconds, whole seconds; entries without a date of their own get a fixed one.
    pub fn modified(&self) -> i64 {
        let at = match self {
            Self::Item(item)
            | Self::IdFile(item)
            | Self::Links(Some(item))
            | Self::Link { item, .. } => item.created_at,
            Self::Nzb(entry) => entry.created_at,
            _ => 0,
        };
        // Matches WebDAV dates and C#/rclone mounts, so exact-mtime scanners see no change.
        if at > 0 {
            at - at % 1000
        } else {
            FIXED_MODIFIED
        }
    }
}

/// 2000-01-01 UTC. Constant so media servers never see fixed folders as changed.
const FIXED_MODIFIED: i64 = 946_684_800_000;

/// Whether `path` is inside one download's completed pointers. Category roots stay read-only.
pub fn ignores_delete(path: &str) -> bool {
    path.strip_prefix("/completed-symlinks/")
        .is_some_and(|rest| rest.split('/').filter(|p| !p.is_empty()).count() > 1)
}

/// Told which segment (flat index across parts) of which file playback zero-filled.
pub type FaultReport = Arc<dyn Fn(ItemId, usize, Fault) + Send + Sync>;

pub struct Vfs {
    db: Db,
    client: Arc<Client>,
    budget: Budget,
    cache: Option<Arc<Cache>>,
    native_cache: Option<Arc<infinidysk_native_cache::Cache>>,
    report: Option<FaultReport>,
    /// Bytes each reader keeps in flight ahead of its position.
    read_ahead: AtomicU64,
    /// Damage caps for gap-tolerant media; off, every file stops at its 3rd fill in a row.
    gap_tolerance: AtomicBool,
    /// Holes of recently read files, shared by all their readers.
    gaps: Mutex<HashMap<ItemId, Arc<Gaps>>>,
    /// Where the tree is mounted for the apps that follow links; link targets start here.
    mount_dir: String,
    sessions: Arc<Sessions>,
    backfills: Arc<Backfills>,
    /// Decoded metadata by blob and [`Db::blob_epoch`], so reopening a file skips
    /// fetching and decoding it.
    metas: Metas,
}

// ponytail: a fixed 64 MB, about 570k segments; make it a setting if libraries outgrow it.
const META_CACHE_BYTES: u64 = 64_000_000;
/// Rough decoded size of one segment: its message id and fields.
const SEGMENT_BYTES: u64 = 112;

/// One shard: each shard caps its entries at its share of the capacity, and one large
/// file's metadata is several MB. Opens are too rare to contend on it.
type Metas = quick_cache::sync::Cache<
    (BlobId, u64),
    Arc<CachedMeta>,
    BySegments,
    RandomState,
    quick_cache::sync::DefaultLifecycle<(BlobId, u64), Arc<CachedMeta>>,
>;

struct CachedMeta {
    file: FileMeta,
    fingerprint: OnceLock<[u8; 32]>,
    repaired_fingerprint: Mutex<Option<(u64, [u8; 32])>>,
}
impl CachedMeta {
    fn base_fingerprint(&self) -> Result<[u8; 32], Error> {
        if let Some(value) = self.fingerprint.get() {
            return Ok(*value);
        }
        let digest = infinidysk_native_cache::Key::fingerprint(&self.file.content_identity()?);
        Ok(*self.fingerprint.get_or_init(|| digest))
    }
    fn fingerprint(&self, client: &Client) -> Result<[u8; 32], Error> {
        let base = self.base_fingerprint()?;
        for _ in 0..2 {
            let generation = client.repair_generation();
            if generation == 0 {
                return Ok(base);
            }
            if let Some((saved, value)) = *self
                .repaired_fingerprint
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                && saved == generation
            {
                return Ok(value);
            }
            let mut hash = None;
            let mut add = |segments: &infinidysk_core::Segments| -> Result<(), Error> {
                for segment in &segments.items {
                    match client.repair_revision(&segment.id) {
                        RepairRevision::Base => {}
                        RepairRevision::Pending => return Err(Error::Repairing),
                        RepairRevision::Repaired(revision) => {
                            let hash = hash.get_or_insert_with(|| {
                                let mut hash = Sha256::new();
                                hash.update(b"infinidysk-file-repairs-v1");
                                hash.update(base);
                                hash
                            });
                            hash.update((segment.id.len() as u64).to_le_bytes());
                            hash.update(&segment.id);
                            hash.update(revision);
                        }
                    }
                }
                Ok(())
            };
            match &self.file {
                FileMeta::Nzb(file) => add(&file.segments)?,
                FileMeta::Multipart(file) => {
                    for part in &file.parts {
                        add(&part.segments)?;
                    }
                }
            }
            let value = hash.map_or(base, |hash| hash.finalize().into());
            if client.repair_generation() == generation {
                *self
                    .repaired_fingerprint
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner) = Some((generation, value));
                return Ok(value);
            }
        }
        Err(Error::Repairing)
    }
}
impl std::ops::Deref for CachedMeta {
    type Target = FileMeta;
    fn deref(&self) -> &FileMeta {
        &self.file
    }
}

#[derive(Clone)]
struct BySegments;

impl quick_cache::Weighter<(BlobId, u64), Arc<CachedMeta>> for BySegments {
    fn weight(&self, _: &(BlobId, u64), meta: &Arc<CachedMeta>) -> u64 {
        let segments = match &meta.file {
            FileMeta::Nzb(f) => f.segments.items.len(),
            FileMeta::Multipart(f) => f.parts.iter().map(|p| p.segments.items.len()).sum(),
        };
        (segments as u64 + 1) * SEGMENT_BYTES
    }
}

impl Vfs {
    /// Readers fetch up to `read_ahead` bytes ahead and share `budget`.
    pub fn new(
        db: Db,
        client: Arc<Client>,
        budget: Budget,
        read_ahead: u64,
        mount_dir: &str,
    ) -> Self {
        Self {
            db,
            client,
            budget,
            cache: None,
            native_cache: None,
            report: None,
            read_ahead: AtomicU64::new(read_ahead),
            gap_tolerance: AtomicBool::new(true),
            gaps: Mutex::default(),
            mount_dir: mount_dir.trim_end_matches('/').to_owned(),
            sessions: Arc::default(),
            backfills: Arc::default(),
            metas: Metas::with_options(
                quick_cache::OptionsBuilder::new()
                    .shards(1)
                    .estimated_items_capacity(1024)
                    .weight_capacity(META_CACHE_BYTES)
                    .build()
                    .expect("both required options are set"),
                BySegments,
                RandomState::new(),
                quick_cache::sync::DefaultLifecycle::default(),
            ),
        }
    }

    /// Nonblocking hints from qualified foreground reads; payloads and storage
    /// operations stay off the handoff path.
    pub fn backfills(&self) -> &Arc<Backfills> {
        &self.backfills
    }

    /// Readers serve and keep segments through `cache`.
    #[must_use]
    pub fn with_cache(mut self, cache: Arc<Cache>) -> Self {
        self.cache = Some(cache);
        self
    }

    /// Uses one final-media cache for every delivery method.
    #[must_use]
    pub fn with_native_cache(mut self, cache: Option<Arc<infinidysk_native_cache::Cache>>) -> Self {
        self.native_cache = cache;
        self
    }

    pub fn warming_budget(&self) -> Option<&Arc<infinidysk_nntp::wire::Budget>> {
        self.client.warming_budget()
    }
    pub fn install_warming_budget(&self, budget: Arc<infinidysk_nntp::wire::Budget>) -> bool {
        self.client.set_warming_budget(budget)
    }
    pub fn foreground_pending(&self) -> bool {
        self.client.foreground_pending()
    }

    pub fn native_cache(&self) -> Option<&Arc<infinidysk_native_cache::Cache>> {
        self.native_cache.as_ref()
    }

    /// Tells `report` about every segment playback could not get.
    #[must_use]
    pub fn with_report(mut self, report: FaultReport) -> Self {
        self.report = Some(report);
        self
    }

    /// Read-ahead for files opened from now on.
    pub fn set_read_ahead(&self, bytes: u64) {
        self.read_ahead.store(bytes, Ordering::Relaxed);
    }

    /// Whether gap-tolerant media may play past damage up to the health caps.
    pub fn set_gap_tolerance(&self, on: bool) {
        self.gap_tolerance.store(on, Ordering::Relaxed);
    }

    /// The file's shared holes, refreshed with those its metadata records.
    fn gaps(&self, item: &Item, meta: &FileMeta) -> Arc<Gaps> {
        let gaps = {
            let mut all = self.gaps.lock().unwrap_or_else(PoisonError::into_inner);
            all.retain(|_, g| Arc::strong_count(g) > 1 || !g.stale());
            Arc::clone(all.entry(item.id).or_default())
        };
        let parts = match meta {
            FileMeta::Nzb(f) => vec![&f.segments],
            FileMeta::Multipart(f) => f.parts.iter().map(|p| &p.segments).collect(),
        };
        let (mut known, mut base) = (BTreeSet::new(), 0);
        let mut repaired = Vec::new();
        for part in parts {
            let flat = |i: &u32| base + usize::try_from(*i).unwrap_or(usize::MAX);
            known.extend(part.missing.iter().chain(&part.corrupt).map(flat));
            for (i, segment) in part.items.iter().enumerate() {
                if matches!(
                    self.client.repair_revision(&segment.id),
                    RepairRevision::Repaired(_)
                ) {
                    repaired.push(base + i);
                }
            }
            base += part.items.len();
        }
        // Archive members' bytes are not the container's, so they get no budget.
        let budget = self.gap_tolerance.load(Ordering::Relaxed)
            && matches!(meta, FileMeta::Nzb(_))
            && media::is_gap_tolerant(&item.name);
        gaps.refresh(known, budget);
        gaps.repaired(repaired);
        gaps
    }

    /// Decoded bytes all readers may hold together.
    pub const fn budget(&self) -> &Budget {
        &self.budget
    }

    pub const fn db(&self) -> &Db {
        &self.db
    }

    /// Item files open now, and traced reads.
    pub const fn sessions(&self) -> &Arc<Sessions> {
        &self.sessions
    }

    /// The node at `path` (`/`-separated, decoded; leading and repeated `/` ignored).
    pub async fn lookup(&self, path: &str) -> Result<Option<Node>, Error> {
        let parts: Vec<&str> = path.split('/').filter(|p| !p.is_empty()).collect();
        if parts.iter().any(|p| *p == "." || *p == "..") {
            return Ok(None);
        }
        let Some((top, rest)) = parts.split_first() else {
            return Ok(Some(Node::Root));
        };
        match *top {
            "README" if rest.is_empty() => Ok(Some(Node::Readme)),
            "content" => Ok(self
                .db
                .item_by_path(&content_path(rest))
                .await?
                .filter(visible)
                .map(Node::Item)),
            ".ids" => self.lookup_id(rest).await,
            "nzbs" => self.lookup_nzb(rest).await,
            "completed-symlinks" => self.lookup_link(rest).await,
            _ => Ok(None),
        }
    }

    async fn lookup_id(&self, rest: &[&str]) -> Result<Option<Node>, Error> {
        let (shards, file) = rest.split_at(rest.len().min(SHARD_DEPTH));
        let mut prefix = String::with_capacity(SHARD_DEPTH);
        for shard in shards {
            match shard.as_bytes() {
                [c] if HEX.contains(c) => prefix.push(char::from(*c)),
                _ => return Ok(None),
            }
        }
        let [name] = file else {
            return Ok(file.is_empty().then_some(Node::Shard(prefix)));
        };
        let Ok(id) = name.parse::<ItemId>() else {
            return Ok(None);
        };
        if *name != id.to_string() || !name.starts_with(&prefix) {
            return Ok(None);
        }
        Ok(self
            .db
            .item(id)
            .await?
            .filter(|i| i.kind != ItemKind::Directory && visible(i))
            .map(Node::IdFile))
    }

    async fn lookup_nzb(&self, rest: &[&str]) -> Result<Option<Node>, Error> {
        let (category, file) = match rest {
            [] => return Ok(Some(Node::Nzbs)),
            [category] => (*category, None),
            [category, file] => (*category, Some(*file)),
            _ => return Ok(None),
        };
        let queue = self.db.queue().await?;
        let mut entries = queue.into_iter().filter(|e| e.category == category);
        Ok(match file {
            None => entries
                .next()
                .map(|_| Node::NzbCategory(category.to_owned())),
            Some(file) => entries.find(|e| e.file_name == file).map(Node::Nzb),
        })
    }

    async fn lookup_link(&self, rest: &[&str]) -> Result<Option<Node>, Error> {
        if rest.is_empty() {
            return Ok(Some(Node::Links(None)));
        }
        let Some(item) = self
            .db
            .item_by_path(&content_path(rest))
            .await?
            .filter(visible)
        else {
            return Ok(None);
        };
        if self.db.completed_link_removed(item.id).await? {
            return Ok(None);
        }
        let job = match rest.len() {
            1 => None,
            2 => Some(item.id),
            _ => self
                .db
                .item_by_path(&content_path(&rest[..2]))
                .await?
                .map(|i| i.id),
        };
        if let Some(job) = job
            && !self.db.is_completed_dir(job).await?
        {
            return Ok(None);
        }
        Ok(match (rest.len(), item.kind) {
            (_, ItemKind::Directory) => Some(Node::Links(Some(item))),
            (1 | 2, _) => None,
            _ => Some(self.link(item)),
        })
    }

    fn link(&self, item: Item) -> Node {
        let target = self.id_link(item.id);
        Node::Link { item, target }
    }

    /// Where a file's `/.ids` entry is under the mount directory; links point here.
    pub fn id_link(&self, id: ItemId) -> String {
        format!("{}/{}", self.mount_dir, id.ids_path())
    }

    /// Children of a directory, in listing order; empty for files.
    pub async fn list(&self, node: &Node) -> Result<Vec<Node>, Error> {
        Ok(match node {
            Node::Root => {
                let content = self
                    .db
                    .item(ItemId::CONTENT)
                    .await?
                    .ok_or(Error::Corrupt("/content is missing"))?;
                vec![
                    Node::Shard(String::new()),
                    Node::Readme,
                    Node::Links(None),
                    Node::Item(content),
                    Node::Nzbs,
                ]
            }
            Node::Item(item) if item.kind == ItemKind::Directory => self
                .db
                .children(item.id)
                .await?
                .into_iter()
                .filter(visible)
                .map(Node::Item)
                .collect(),
            Node::Shard(prefix) if prefix.len() < SHARD_DEPTH => HEX
                .iter()
                .map(|c| Node::Shard(format!("{prefix}{}", char::from(*c))))
                .collect(),
            Node::Shard(prefix) => {
                let (start, end) = id_range(prefix)?;
                self.db
                    .files_in_id_range(start, end)
                    .await?
                    .into_iter()
                    .filter(visible)
                    .map(Node::IdFile)
                    .collect()
            }
            Node::Nzbs => {
                let mut categories: Vec<String> = self
                    .db
                    .queue()
                    .await?
                    .into_iter()
                    .map(|e| e.category)
                    .filter(|c| !c.is_empty() && !c.contains('/'))
                    .collect();
                categories.sort_unstable();
                categories.dedup();
                categories.into_iter().map(Node::NzbCategory).collect()
            }
            Node::NzbCategory(category) => {
                let mut entries: Vec<QueueEntry> = self
                    .db
                    .queue()
                    .await?
                    .into_iter()
                    .filter(|e| &e.category == category && !e.file_name.contains('/'))
                    .collect();
                entries.sort_by(|a, b| a.file_name.cmp(&b.file_name));
                entries.into_iter().map(Node::Nzb).collect()
            }
            Node::Links(None) => self
                .db
                .children(ItemId::CONTENT)
                .await?
                .into_iter()
                .filter(|i| i.kind == ItemKind::Directory)
                .map(|i| Node::Links(Some(i)))
                .collect(),
            Node::Links(Some(dir)) if dir.parent == Some(ItemId::CONTENT) => self
                .db
                .completed_dirs(dir.id)
                .await?
                .into_iter()
                .map(|i| Node::Links(Some(i)))
                .collect(),
            Node::Links(Some(dir)) => self
                .db
                .completed_link_children(dir.id)
                .await?
                .into_iter()
                .filter(visible)
                .map(|i| match i.kind {
                    ItemKind::Directory => Node::Links(Some(i)),
                    _ => self.link(i),
                })
                .collect(),
            _ => Vec::new(),
        })
    }

    /// Remove a completed pointer, preserving `/content`, `/.ids`, blobs and history.
    /// Already-removed pointers are accepted so Arr cleanup retries are safe.
    pub async fn remove_completed_link(&self, path: &str) -> Result<bool, Error> {
        if !ignores_delete(path) {
            return Ok(false);
        }
        match self.lookup(path).await? {
            Some(Node::Link { item, .. } | Node::Links(Some(item))) => {
                self.db.remove_completed_links(item.id).await?;
            }
            None => {}
            _ => return Ok(false),
        }
        Ok(true)
    }

    /// Source identity for final-media caching; never reads Usenet or payload volumes.
    pub async fn cache_key(&self, item: &Item) -> Result<infinidysk_native_cache::Key, Error> {
        let meta = self.meta(item).await?;
        Ok(infinidysk_native_cache::Key::from_fingerprint(
            item.id.0,
            meta.fingerprint(&self.client)?,
            item.size.unwrap_or(0),
        ))
    }

    /// A reader over a file's bytes; `None` for directories. Item files are listed in
    /// [`sessions`](Self::sessions) while open.
    pub async fn open(&self, node: &Node, origin: Origin) -> Result<Option<Handle>, Error> {
        let memory =
            |bytes: Vec<u8>| Ok(Some(Handle::new(Reader::Memory(Cursor::new(bytes)), None)));
        match node {
            Node::Readme => memory(README.as_bytes().to_vec()),
            Node::Link { target, .. } => memory(target.clone().into_bytes()),
            Node::Nzb(entry) => {
                let raw = self
                    .db
                    .blob(entry.nzb_blob)
                    .await?
                    .ok_or(Error::Corrupt("NZB blob is missing"))?;
                memory(blob::decode_nzb(&raw)?)
            }
            Node::Item(item) | Node::IdFile(item) if item.kind != ItemKind::Directory => {
                let session = self
                    .sessions
                    .start(item.id, item.path.clone(), item.size.unwrap_or(0), origin)
                    .ok_or(Error::Repairing)?;
                let current = self
                    .db
                    .item(item.id)
                    .await?
                    .filter(visible)
                    .ok_or(Error::Repairing)?;
                let item = &current;
                let meta = self.meta(item).await?;
                if !session.bind_source(&meta) {
                    return Err(Error::Repairing);
                }
                let fingerprint = meta.fingerprint(&self.client)?;
                let trace = session.trace();
                let report = self.report.as_ref().map(|report| {
                    let (report, id) = (Arc::clone(report), item.id);
                    Arc::new(move |index, fault| report(id, index, fault)) as Report
                });
                let gaps = self.gaps(item, &meta);
                let reader = Reader::new(
                    &meta,
                    &self.client,
                    &self.budget,
                    self.cache.as_ref(),
                    self.read_ahead.load(Ordering::Relaxed),
                    trace.as_ref(),
                    report.as_ref(),
                )?
                .with_filler(&meta, &item.name)
                .with_gaps(Arc::clone(&gaps))
                .with_watch(session.watch());
                let reader = match &self.native_cache {
                    Some(cache) => {
                        let key = infinidysk_native_cache::Key::from_fingerprint(
                            item.id.0,
                            fingerprint,
                            item.size.unwrap_or(0),
                        );
                        native::NativeReader::wrap(
                            reader,
                            Arc::clone(cache),
                            key,
                            gaps,
                            session.native_cache_counter(),
                            Arc::clone(&self.backfills),
                        )
                    }
                    None => reader,
                };
                let reader = revision::RevisionReader::wrap(
                    reader,
                    meta,
                    Arc::clone(&self.client),
                    fingerprint,
                );
                Ok(Some(Handle::new(reader, Some(session))))
            }
            _ => Ok(None),
        }
    }

    /// A reader for media analysis: no session, cache, fault reports or read-ahead, and
    /// reads fail instead of zero-filling.
    pub async fn open_analysis(&self, item: &Item) -> Result<Reader, Error> {
        let meta = self.meta(item).await?;
        let fingerprint = meta.fingerprint(&self.client)?;
        let reader = self.open_meta(&meta)?;
        Ok(revision::RevisionReader::wrap(
            reader,
            meta,
            Arc::clone(&self.client),
            fingerprint,
        ))
    }

    /// Background warming has no playback session, filler or hedges. Its bounded
    /// article window follows spare provider capacity and stops at the requested end.
    pub async fn open_warming(&self, item: &Item, floor: usize) -> Result<Reader, Error> {
        let report = self.report.as_ref().map(|report| {
            let (report, id) = (Arc::clone(report), item.id);
            Arc::new(move |index, fault| report(id, index, fault)) as Report
        });
        let meta = self.meta(item).await?;
        let fingerprint = meta.fingerprint(&self.client)?;
        let reader = Reader::new(
            &meta,
            &self.client,
            &self.budget,
            self.cache.as_ref(),
            0,
            None,
            report.as_ref(),
        )?
        .into_warming(floor);
        Ok(revision::RevisionReader::wrap(
            reader,
            meta,
            Arc::clone(&self.client),
            fingerprint,
        ))
    }

    /// The file's decoded metadata; concurrent first opens fetch it once.
    async fn meta(&self, item: &Item) -> Result<Arc<CachedMeta>, Error> {
        let id = item
            .meta_blob
            .ok_or(Error::Corrupt("file has no metadata"))?;
        // Read before the blob, so an update racing this load lands under a newer epoch.
        let key = (id, self.db.blob_epoch());
        self.metas
            .get_or_insert_async(&key, async {
                let raw = self
                    .db
                    .blob(id)
                    .await?
                    .ok_or(Error::Corrupt("metadata blob is missing"))?;
                let file = blob::decode_meta(&raw)?;

                Ok(Arc::new(CachedMeta {
                    file,
                    fingerprint: OnceLock::new(),
                    repaired_fingerprint: Mutex::new(None),
                }))
            })
            .await
    }

    /// Like [`open_analysis`](Self::open_analysis), for a file not stored yet.
    pub fn open_meta(&self, meta: &FileMeta) -> Result<Reader, Error> {
        Ok(Reader::new(meta, &self.client, &self.budget, None, 0, None, None)?.into_strict())
    }
}

/// Hidden files wait for their replacement and are not served.
fn visible(item: &Item) -> bool {
    item.health_state != HealthState::Hidden
}

/// `/content/<rest…>` for path components `rest`.
fn content_path(rest: &[&str]) -> String {
    let mut path = "/content".to_owned();
    for part in rest {
        path.push('/');
        path.push_str(part);
    }
    path
}

/// Ids starting with the hex `prefix`, as `start..end` (`None` = unbounded).
fn id_range(prefix: &str) -> Result<(ItemId, Option<ItemId>), Error> {
    let bits = 4 * u32::try_from(prefix.len()).map_err(|_| Error::Corrupt("shard prefix"))?;
    let value = u128::from_str_radix(prefix, 16).map_err(|_| Error::Corrupt("shard prefix"))?;
    let shift = 128 - bits;
    let end = value
        .checked_add(1)
        .filter(|end| end.leading_zeros() >= 128 - bits)
        .map(|end| ItemId(Uuid::from_u128(end << shift)));
    Ok((ItemId(Uuid::from_u128(value << shift)), end))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn id_ranges_cover_exactly_the_prefix() {
        let (start, end) = id_range("abcde").unwrap_or_else(|e| panic!("{e}"));
        assert_eq!(start.0, Uuid::from_u128(0xabcde << 108));
        assert_eq!(end.map(|e| e.0), Some(Uuid::from_u128(0xabcdf << 108)));
        assert_eq!(id_range("fffff").map(|r| r.1).ok(), Some(None));
    }
}
