//! Catalogue-only browsing of the exclusively owned original store.
use super::{Entry, Legacy, Writer};
use crate::Error;
use serde::{Deserialize, Serialize};
use sqlx::FromRow;
use utoipa::ToSchema;

#[derive(Clone, Debug, Serialize, ToSchema, FromRow)]
#[schema(as=LegacyCacheInventoryEntry)]
pub struct InventoryEntry {
    pub key: String,
    pub item: String,
    pub name: Option<String>,
    #[sqlx(default)]
    pub source_present: bool,
    pub folder: String,
    pub size: i64,
    pub cached_bytes: i64,
    pub allocated_bytes: i64,
    pub pending_bytes: i64,
    pub dirty: i64,
    pub pinned: i64,
    pub last_used_ms: i64,
    pub generation: Option<String>,
    pub chunk: i64,
}
impl InventoryEntry {
    pub fn identity(&self) -> Entry {
        Entry {
            key: self.key.clone(),
            folder: self.folder.clone(),
            item: self.item.clone(),
            size: self.size,
            pinned: self.pinned,
            generation: self.generation.clone(),
            chunk: self.chunk,
        }
    }
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=LegacyCacheInventoryFolder)]
pub struct InventoryFolder {
    pub id: String,
    pub path: String,
    pub files: i64,
    pub cached_bytes: i64,
    pub allocated_bytes: i64,
    pub pending_bytes: i64,
    pub retired_bytes: i64,
    pub held_files: i64,
    pub writable: bool,
    pub eviction_enabled: bool,
    pub quota_bytes: Option<u64>,
    pub min_free_bytes: Option<u64>,
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=LegacyCacheInventory)]
pub struct Inventory {
    pub folders: Vec<InventoryFolder>,
    pub entries: Vec<InventoryEntry>,
    pub next: Option<String>,
    pub matched_files: i64,
    pub writable: bool,
    pub recency_pending: usize,
    pub recency_dropped: u64,
}
#[derive(Clone, Debug, Deserialize, Serialize, ToSchema, PartialEq, Eq)]
#[schema(as=LegacyCacheEntryIdentity)]
pub struct EntryIdentity {
    pub key: String,
    pub folder: String,
    pub item: String,
    pub size: i64,
    pub generation: Option<String>,
    pub chunk: i64,
}
impl From<&Entry> for EntryIdentity {
    fn from(e: &Entry) -> Self {
        Self {
            key: e.key.clone(),
            folder: e.folder.clone(),
            item: e.item.clone(),
            size: e.size,
            generation: e.generation.clone(),
            chunk: e.chunk,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=LegacyCacheSort)]
pub enum InventorySort {
    #[default]
    Key,
    Recent,
    Name,
    Coverage,
}
impl InventorySort {
    fn text(self) -> &'static str {
        match self {
            Self::Key => "key",
            Self::Recent => "recent",
            Self::Name => "name",
            Self::Coverage => "coverage",
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=LegacyCacheCoverageFilter)]
pub enum InventoryFilter {
    #[default]
    All,
    Cached,
    Complete,
    Partial,
    Empty,
    Held,
}
impl InventoryFilter {
    fn text(self) -> &'static str {
        match self {
            Self::All => "all",
            Self::Cached => "cached",
            Self::Complete => "complete",
            Self::Partial => "partial",
            Self::Empty => "empty",
            Self::Held => "held",
        }
    }
}
#[derive(Default)]
pub struct Browse<'a> {
    pub after: &'a str,
    pub folder: Option<&'a str>,
    pub search: &'a str,
    pub limit: u32,
    pub sort: InventorySort,
    pub coverage: InventoryFilter,
    /// Current labels keyed by canonical 32-digit item IDs; used only in the read view.
    pub names: Option<&'a std::collections::HashMap<String, String>>,
}
#[derive(Serialize, Deserialize)]
struct Cursor {
    key: String,
    access: i64,
    name: String,
    ratio: f64,
    sort: InventorySort,
    coverage: InventoryFilter,
    folder: Option<String>,
    search: String,
}
impl Cursor {
    fn new(q: &Browse<'_>) -> Self {
        Self {
            key: String::new(),
            access: 0,
            name: String::new(),
            ratio: 0.,
            sort: q.sort,
            coverage: q.coverage,
            folder: q.folder.map(str::to_owned),
            search: q.search.to_owned(),
        }
    }
}
#[derive(FromRow)]
struct BrowseRow {
    #[sqlx(flatten)]
    entry: InventoryEntry,
    sort_name: String,
    sort_ratio: f64,
}
fn literal_search(search: &str) -> String {
    format!(
        "%{}%",
        search
            .replace('!', "!!")
            .replace('%', "!%")
            .replace('_', "!_")
    )
}
#[derive(Clone, Debug, Serialize, ToSchema, FromRow)]
#[schema(as=LegacyCacheEviction)]
pub struct OriginalEviction {
    pub cursor: i64,
    pub key: String,
    pub folder: String,
    pub item: String,
    pub name: Option<String>,
    pub size: i64,
    pub allocated_bytes: i64,
    pub cached_bytes: i64,
    pub reason: String,
    pub at_ms: i64,
}
#[derive(Serialize, ToSchema)]
#[schema(as=LegacyCacheEvictionPage)]
pub struct OriginalEvictions {
    pub entries: Vec<OriginalEviction>,
    pub next: Option<i64>,
    pub total: i64,
    pub available: bool,
}
impl Legacy {
    pub async fn inventory(
        &self,
        after: &str,
        folder: Option<&str>,
        search: &str,
        limit: u32,
        writer: Option<&Writer>,
    ) -> Result<Inventory, Error> {
        self.inventory_page(
            &Browse {
                after,
                folder,
                search,
                limit,
                ..Browse::default()
            },
            writer,
        )
        .await
    }
    pub async fn inventory_page(
        &self,
        query: &Browse<'_>,
        writer: Option<&Writer>,
    ) -> Result<Inventory, Error> {
        let limit = query.limit.clamp(1, 100);
        if query.after.len() > 8192
            || query.search.len() > 256
            || query.folder.is_some_and(|s| s.len() > 256)
        {
            return Err(Error::Invalid("cache browse request too long"));
        }
        let cursor = if query.after.is_empty() {
            None
        } else if query.sort == InventorySort::Key {
            Some(Cursor {
                key: query.after.to_owned(),
                ..Cursor::new(query)
            })
        } else {
            let c: Cursor = serde_json::from_str(query.after)
                .map_err(|_| Error::Invalid("invalid cache cursor"))?;
            if c.sort != query.sort
                || c.coverage != query.coverage
                || c.folder.as_deref() != query.folder
                || c.search != query.search
            {
                return Err(Error::Invalid(
                    "cache cursor filters changed; refresh the file list",
                ));
            }
            Some(c)
        };
        if cursor
            .as_ref()
            .is_some_and(|c| c.key.len() > 64 || !c.ratio.is_finite() || c.name.len() > 4096)
        {
            return Err(Error::Invalid("invalid cache cursor"));
        }
        // ponytail: resolve at most 2000 historical missing labels per request; older
        // unresolved entries retain their explicit item IDs until labelled by maintenance.
        if query.names.is_some_and(|n| n.len() > 2000) {
            return Err(Error::Invalid("too many cache name aliases"));
        }
        let aliases = serde_json::to_string(&query.names.cloned().unwrap_or_default())
            .map_err(|_| Error::Invalid("cannot encode cache names"))?;
        let search = literal_search(query.search);
        let matched_files:i64=sqlx::query_scalar("WITH aliases AS MATERIALIZED (SELECT key,value FROM json_each($1)), inventory AS (SELECT e.*,COALESCE(NULLIF(e.DisplayName,''),a.value,e.ItemId) AS BrowseName FROM Entries e LEFT JOIN aliases a ON a.key=replace(lower(e.ItemId),'-','')) SELECT COUNT(*) FROM inventory WHERE ($2 IS NULL OR Folder=$2) AND (BrowseName LIKE $3 ESCAPE '!' OR ItemId LIKE $3 ESCAPE '!') AND ($4='all' OR ($4='cached' AND VerifiedBytes>0) OR ($4='complete' AND Length>0 AND VerifiedBytes>=Length) OR ($4='partial' AND VerifiedBytes>0 AND VerifiedBytes<Length) OR ($4='empty' AND VerifiedBytes=0) OR ($4='held' AND (Pinned<>0 OR Dirty<>0 OR PendingBytes<>0)))")
            .bind(&aliases).bind(query.folder).bind(&search).bind(query.coverage.text()).fetch_one(&self.db).await?;
        let empty = Cursor::new(query);
        let c = cursor.as_ref().unwrap_or(&empty);
        let mut page:Vec<BrowseRow>=sqlx::query_as("WITH aliases AS MATERIALIZED (SELECT key,value FROM json_each($1)), inventory AS (SELECT e.*,COALESCE(NULLIF(e.DisplayName,''),a.value,e.ItemId) AS BrowseName FROM Entries e LEFT JOIN aliases a ON a.key=replace(lower(e.ItemId),'-','')) SELECT Key AS key, ItemId AS item, BrowseName AS name, Folder AS folder, Length AS size, VerifiedBytes AS cached_bytes, Bytes AS allocated_bytes, PendingBytes AS pending_bytes, Dirty AS dirty, Pinned AS pinned, Access*300000 AS last_used_ms, Generation AS generation, ChunkSize AS chunk, lower(BrowseName) AS sort_name, CASE WHEN Length>0 THEN CAST(VerifiedBytes AS REAL)/Length ELSE 0 END AS sort_ratio FROM inventory WHERE ($2 IS NULL OR Folder=$2) AND (BrowseName LIKE $3 ESCAPE '!' OR ItemId LIKE $3 ESCAPE '!') AND ($4='all' OR ($4='cached' AND VerifiedBytes>0) OR ($4='complete' AND Length>0 AND VerifiedBytes>=Length) OR ($4='partial' AND VerifiedBytes>0 AND VerifiedBytes<Length) OR ($4='empty' AND VerifiedBytes=0) OR ($4='held' AND (Pinned<>0 OR Dirty<>0 OR PendingBytes<>0))) AND ($5='' OR ($6='key' AND Key>$5) OR ($6='recent' AND (Access*300000<$7 OR (Access*300000=$7 AND Key>$5))) OR ($6='name' AND (lower(BrowseName)>$8 OR (lower(BrowseName)=$8 AND Key>$5))) OR ($6='coverage' AND ((CASE WHEN Length>0 THEN CAST(VerifiedBytes AS REAL)/Length ELSE 0 END)<$9 OR ((CASE WHEN Length>0 THEN CAST(VerifiedBytes AS REAL)/Length ELSE 0 END)=$9 AND Key>$5)))) ORDER BY CASE WHEN $6='recent' THEN Access END DESC, CASE WHEN $6='name' THEN lower(BrowseName) END ASC, CASE WHEN $6='coverage' THEN CASE WHEN Length>0 THEN CAST(VerifiedBytes AS REAL)/Length ELSE 0 END END DESC, Key ASC LIMIT $10")
            .bind(&aliases).bind(query.folder).bind(&search).bind(query.coverage.text()).bind(&c.key).bind(query.sort.text()).bind(c.access).bind(&c.name).bind(c.ratio).bind(i64::from(limit)+1).fetch_all(&self.db).await?;
        let next = if page.len() > limit as usize {
            page.truncate(limit as usize);
            page.last()
                .map(|r| {
                    if query.sort == InventorySort::Key {
                        Ok(r.entry.key.clone())
                    } else {
                        let mut c = Cursor::new(query);
                        c.key.clone_from(&r.entry.key);
                        c.access = r.entry.last_used_ms;
                        c.name.clone_from(&r.sort_name);
                        c.ratio = r.sort_ratio;
                        serde_json::to_string(&c)
                            .map_err(|_| Error::Invalid("cannot encode cache cursor"))
                    }
                })
                .transpose()?
        } else {
            None
        };
        let entries = page.into_iter().map(|r| r.entry).collect();
        let mut folders = Vec::new();
        for (id, root) in self.roots.iter() {
            let (files, cached, pending, held): (i64, i64, i64, i64) = sqlx::query_as(
                "SELECT COUNT(*),COALESCE(SUM(VerifiedBytes),0),COALESCE(SUM(PendingBytes),0),COALESCE(SUM(CASE WHEN Pinned<>0 OR Dirty<>0 OR PendingBytes<>0 THEN 1 ELSE 0 END),0) FROM Entries WHERE Folder=$1",
            )
            .bind(id)
            .fetch_one(&self.db)
            .await?;
            let allocated: Option<i64> =
                sqlx::query_scalar("SELECT Bytes FROM FolderTotals WHERE Folder=$1")
                    .bind(id)
                    .fetch_optional(&self.db)
                    .await?;
            let retired: i64 = sqlx::query_scalar(
                "SELECT COALESCE(SUM(Bytes),0) FROM RetiredEntries WHERE Folder=$1",
            )
            .bind(id)
            .fetch_one(&self.db)
            .await?;
            let option = writer.and_then(|w| w.settings().folders.iter().find(|f| f.id == *id));
            folders.push(InventoryFolder {
                id: id.clone(),
                path: root.path.clone(),
                files,
                cached_bytes: cached,
                allocated_bytes: allocated.unwrap_or(0),
                pending_bytes: pending,
                retired_bytes: retired,
                held_files: held,
                writable: self.writable && option.is_some(),
                eviction_enabled: option.is_some_and(|f| f.eviction.is_some()),
                quota_bytes: option.map(|f| f.quota_bytes),
                min_free_bytes: option.map(|f| f.min_free_bytes),
            });
        }
        folders.sort_by(|a, b| a.id.cmp(&b.id));
        Ok(Inventory {
            folders,
            entries,
            next,
            matched_files,
            writable: self.writable,
            recency_pending: self.recency.as_ref().map_or(0, |r| r.pending()),
            recency_dropped: self.recency.as_ref().map_or(0, |r| r.dropped()),
        })
    }
    pub async fn unnamed_items(&self) -> Result<Vec<String>, Error> {
        Ok(sqlx::query_scalar("SELECT DISTINCT ItemId FROM Entries WHERE COALESCE(DisplayName,'')='' ORDER BY ItemId LIMIT 2000").fetch_all(&self.db).await?)
    }
    pub async fn exact_entry(&self, identity: &EntryIdentity) -> Result<Option<Entry>, Error> {
        let entry:Option<Entry>=sqlx::query_as("SELECT Key AS key,Folder AS folder,ItemId AS item,Length AS size,Pinned AS pinned,Generation AS generation,ChunkSize AS chunk FROM Entries WHERE Key=$1").bind(&identity.key).fetch_optional(&self.db).await?;
        Ok(entry.filter(|e| EntryIdentity::from(e) == *identity))
    }
    /// Manual unpin cannot clear a health pin or an unknown future hold.
    pub async fn manual_pin(&self, identity: &EntryIdentity, pin: bool) -> Result<bool, Error> {
        if !self.writable {
            return Ok(false);
        }
        let _guard = self.entry_lock(&identity.key).read_owned().await;
        if self.exact_entry(identity).await?.is_none() {
            return Ok(false);
        }
        Ok(sqlx::query("UPDATE Entries SET Pinned=$1 WHERE Key=$2 AND Pinned=$3 AND PendingBytes=0 AND Dirty=0")
            .bind(i64::from(pin)).bind(&identity.key).bind(i64::from(!pin)).execute(&self.db).await?.rows_affected()>0)
    }
}

#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=LegacyCacheStorageDiagnostic)]
pub struct StorageDiagnostic {
    pub id: String,
    pub identity_ok: Option<bool>,
    pub free_bytes: Option<u64>,
    pub read_only: Option<bool>,
    pub write_permitted: Option<bool>,
}
impl Legacy {
    /// A timed-out probe retains its owner and concurrency permit until actual I/O ends.
    pub async fn diagnostics(&self) -> Vec<StorageDiagnostic> {
        let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(5);
        let mut probes = Vec::new();
        for (id, root) in self.roots.iter() {
            let task = if let Ok(permit) = self.diagnostic_slots.clone().try_acquire_owned() {
                let root = root.clone();
                Some(tokio::task::spawn_blocking(move || {
                    let _permit = permit;
                    root.validate()?;
                    let stat = nix::sys::statvfs::fstatvfs(&root.file)?;
                    root.validate()?;
                    Ok::<_, Error>((
                        crate::filesystem::bytes(stat.blocks_available(), stat.fragment_size()),
                        stat.flags().contains(nix::sys::statvfs::FsFlags::ST_RDONLY),
                        nix::unistd::access(root.path.as_str(), nix::unistd::AccessFlags::W_OK)
                            .is_ok(),
                    ))
                }))
            } else {
                None
            };
            probes.push((id.clone(), task));
        }
        let mut result = Vec::new();
        for (id, task) in probes {
            let (identity_ok, free_bytes, read_only, write_permitted) = if let Some(task) = task {
                match tokio::time::timeout_at(deadline, task).await {
                    Ok(Ok(Ok((free, read_only, write)))) => {
                        (Some(true), Some(free), Some(read_only), Some(write))
                    }
                    Ok(Ok(Err(_))) => (Some(false), None, None, None),
                    _ => (None, None, None, None),
                }
            } else {
                (None, None, None, None)
            };
            result.push(StorageDiagnostic {
                id,
                identity_ok,
                free_bytes,
                read_only,
                write_permitted,
            });
        }
        result.sort_by(|a, b| a.id.cmp(&b.id));
        result
    }
}

impl Legacy {
    /// Hash-check one committed block without provider traffic or payload writes.
    pub async fn probe(&self, identity: &EntryIdentity, offset: u64) -> Result<Option<u64>, Error> {
        if !offset.is_multiple_of(crate::BLOCK) {
            return Err(Error::Invalid("cache sample offset must be block aligned"));
        }
        let entry = self.exact_entry(identity).await?.ok_or(Error::Invalid(
            "cache entry changed; refresh before checking",
        ))?;
        let permit = self
            .diagnostic_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| Error::Invalid("cache storage checks are busy"))?;
        Ok(self
            .read_block(&entry, offset / crate::BLOCK, permit)
            .await?
            .map(|b| b.len() as u64))
    }
}

impl Legacy {
    /// Populate a missing display label without changing payload identity or existing labels.
    pub async fn label(&self, identity: &EntryIdentity, name: &str) -> Result<bool, Error> {
        if !self.writable {
            return Ok(false);
        }
        let _guard = self.entry_lock(&identity.key).read_owned().await;
        if self.exact_entry(identity).await?.is_none() {
            return Ok(false);
        }
        Ok(sqlx::query(
            "UPDATE Entries SET DisplayName=$1 WHERE Key=$2 AND COALESCE(DisplayName,'')=''",
        )
        .bind(name)
        .bind(&identity.key)
        .execute(&self.db)
        .await?
        .rows_affected()
            > 0)
    }
}

impl Legacy {
    pub async fn original_evictions(
        &self,
        before: Option<i64>,
        search: &str,
        reason: Option<&str>,
        folder: Option<&str>,
        limit: u32,
    ) -> Result<OriginalEvictions, Error> {
        if search.len() > 256
            || reason.is_some_and(|s| s.len() > 128)
            || folder.is_some_and(|s| s.len() > 256)
            || before.is_some_and(|v| v < 1)
        {
            return Err(Error::Invalid("invalid eviction history filter"));
        }
        let exists:bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='CacheEvictions')").fetch_one(&self.db).await?;
        if !exists {
            return Ok(OriginalEvictions {
                entries: vec![],
                next: None,
                total: 0,
                available: false,
            });
        }
        let search = literal_search(search);
        let limit = limit.clamp(1, 100);
        let total:i64=sqlx::query_scalar("SELECT COUNT(*) FROM CacheEvictions WHERE (COALESCE(DisplayName,'') LIKE $1 ESCAPE '!' OR ItemId LIKE $1 ESCAPE '!') AND ($2 IS NULL OR Reason=$2) AND ($3 IS NULL OR Folder=$3)").bind(&search).bind(reason).bind(folder).fetch_one(&self.db).await?;
        let mut entries:Vec<OriginalEviction>=sqlx::query_as("SELECT Id AS cursor,Key AS key,Folder AS folder,ItemId AS item,DisplayName AS name,Length AS size,AllocatedBytes AS allocated_bytes,VerifiedBytes AS cached_bytes,Reason AS reason,EvictedUnix*1000 AS at_ms FROM CacheEvictions WHERE Id<$1 AND (COALESCE(DisplayName,'') LIKE $2 ESCAPE '!' OR ItemId LIKE $2 ESCAPE '!') AND ($3 IS NULL OR Reason=$3) AND ($4 IS NULL OR Folder=$4) ORDER BY Id DESC LIMIT $5")
            .bind(before.unwrap_or(i64::MAX)).bind(&search).bind(reason).bind(folder).bind(i64::from(limit)+1).fetch_all(&self.db).await?;
        let next = if entries.len() > limit as usize {
            entries.truncate(limit as usize);
            entries.last().map(|r| r.cursor)
        } else {
            None
        };
        Ok(OriginalEvictions {
            entries,
            next,
            total,
            available: true,
        })
    }
}
