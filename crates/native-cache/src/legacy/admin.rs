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
impl Legacy {
    pub async fn inventory(
        &self,
        after: &str,
        folder: Option<&str>,
        search: &str,
        limit: u32,
        writer: Option<&Writer>,
    ) -> Result<Inventory, Error> {
        let limit = limit.clamp(1, 100);
        // LIKE wildcards are escaped: the search box always means literal text.
        let search = format!(
            "%{}%",
            search
                .replace('!', "!!")
                .replace('%', "!%")
                .replace('_', "!_")
        );
        let mut entries:Vec<InventoryEntry>=sqlx::query_as("SELECT Key AS key, ItemId AS item, DisplayName AS name, Folder AS folder, Length AS size, VerifiedBytes AS cached_bytes, Bytes AS allocated_bytes, PendingBytes AS pending_bytes, Dirty AS dirty, Pinned AS pinned, Access*300000 AS last_used_ms, Generation AS generation, ChunkSize AS chunk FROM Entries WHERE Key>$1 AND ($2 IS NULL OR Folder=$2) AND (COALESCE(DisplayName,'') LIKE $3 ESCAPE '!' OR ItemId LIKE $3 ESCAPE '!') ORDER BY Key LIMIT $4")
            .bind(after).bind(folder).bind(search).bind(i64::from(limit)+1).fetch_all(&self.db).await?;
        let next = if entries.len() > limit as usize {
            entries.truncate(limit as usize);
            entries.last().map(|e| e.key.clone())
        } else {
            None
        };
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
            writable: self.writable,
            recency_pending: self.recency.as_ref().map_or(0, |r| r.pending()),
            recency_dropped: self.recency.as_ref().map_or(0, |r| r.dropped()),
        })
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
