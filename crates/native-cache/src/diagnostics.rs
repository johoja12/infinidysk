//! Catalogue-first diagnostics. Payload probes never fetch provider bytes or invent
//! coverage, and uncertain storage I/O leaves the committed block record intact.
use super::{Cache, Error, Key, now};
use serde::Serialize;
use std::sync::Arc;
use utoipa::ToSchema;
use uuid::Uuid;

#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as = NativeCacheEviction)]
pub struct Eviction {
    pub cursor: i64,
    pub item: Uuid,
    pub revision: String,
    pub bytes: u64,
    pub at_ms: i64,
    pub reason: String,
}

#[derive(Clone, Debug, Default, Serialize, ToSchema)]
#[schema(as = NativeCacheProbe)]
pub struct Probe {
    /// Pass this as the next request's `after_block`. None means this pass is complete.
    pub next_block: Option<u64>,
    pub checked: u32,
    pub verified: u32,
    pub invalidated: u32,
    pub unavailable: u32,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as = NativeCacheFolderDiagnostic)]
pub struct FolderDiagnostic {
    pub folder_id: Uuid,
    pub read_only: bool,
    pub identity_ok: Option<bool>,
    pub free_bytes: Option<u64>,
    pub committed_bytes: u64,
    pub invalid_blocks: u64,
    pub quota_bytes: u64,
    pub minimum_free_bytes: u64,
    pub checked_at_ms: i64,
}

impl Cache {
    /// Newest first, with a stable row cursor even when newer evictions arrive.
    pub async fn evictions(&self, before: Option<i64>, limit: u32) -> Result<Vec<Eviction>, Error> {
        type Row = (i64, String, String, i64, i64, String);
        let rows: Vec<Row> = sqlx::query_as("SELECT rowid,item,revision,bytes,at,reason FROM evictions WHERE rowid < $1 ORDER BY rowid DESC LIMIT $2")
            .bind(before.unwrap_or(i64::MAX)).bind(limit.clamp(1, 200)).fetch_all(&self.db).await?;
        rows.into_iter()
            .map(|(cursor, item, revision, bytes, at_ms, reason)| {
                Ok(Eviction {
                    cursor,
                    item: Uuid::parse_str(&item)
                        .map_err(|_| Error::Invalid("invalid evicted item identity"))?,
                    revision,
                    bytes: bytes.cast_unsigned(),
                    at_ms,
                    reason,
                })
            })
            .collect()
    }

    /// Verifies at most 16 committed blocks. A confirmed bad block loses coverage;
    /// healthy bytes become readable again only through a later verified fill.
    pub async fn probe(&self, key: &Key, after_block: u64, limit: u32) -> Result<Probe, Error> {
        let start =
            i64::try_from(after_block).map_err(|_| Error::Invalid("invalid probe cursor"))?;
        let limit = limit.clamp(1, 16);
        let blocks: Vec<i64> = sqlx::query_scalar("SELECT block FROM blocks WHERE key=$1 AND valid=1 AND block >= $2 ORDER BY block LIMIT $3")
            .bind(key.id()).bind(start).bind(limit + 1).fetch_all(&self.db).await?;
        let lease = self
            .lease(key.clone())
            .ok_or(Error::Invalid("cache entry is being evicted"))?;
        let mut result = Probe::default();
        // Bound diagnostic concurrency with the existing buffer allowance. The file
        // read retains its own permit/lease until blocking I/O really completes.
        let _permit = self
            .try_buffer()
            .ok_or(Error::Invalid("cache is busy; retry probe later"))?;
        for &block in blocks.iter().take(limit as usize) {
            result.checked += 1;
            if self.read(&lease, block.cast_unsigned()).await?.is_some() {
                result.verified += 1;
            } else {
                let valid: Option<i64> =
                    sqlx::query_scalar("SELECT valid FROM blocks WHERE key=$1 AND block=$2")
                        .bind(key.id())
                        .bind(block)
                        .fetch_optional(&self.db)
                        .await?;
                if valid == Some(0) {
                    result.invalidated += 1;
                } else {
                    result.unavailable += 1;
                }
            }
            // Retry unavailable storage on the same block; it must not disappear from
            // a resumed probe just because its mount was temporarily unavailable.
            if result.unavailable > 0 {
                result.next_block = Some(block.cast_unsigned());
                return Ok(result);
            }
        }
        result.next_block = blocks.get(limit as usize).map(|v| v.cast_unsigned());
        Ok(result)
    }

    /// Bounded, read-only identity/free-space checks; no payload walk or write probe.
    pub async fn diagnostics(&self) -> Result<Vec<FolderDiagnostic>, Error> {
        let mut result = Vec::new();
        for folder in &self.folders {
            let (bytes, invalid): (i64,i64) = sqlx::query_as("SELECT COALESCE(SUM(CASE WHEN b.valid=1 THEN b.len ELSE 0 END),0),COALESCE(SUM(CASE WHEN b.valid=0 THEN 1 ELSE 0 END),0) FROM blocks b JOIN entries e ON e.key=b.key WHERE e.folder=$1")
                .bind(folder.settings.id.to_string()).fetch_one(&self.db).await?;
            let permit = self
                .try_buffer()
                .ok_or(Error::Invalid("cache is busy; retry diagnostics later"))?;
            let folder_job = Arc::clone(folder);
            let job = tokio::task::spawn_blocking(move || {
                let _permit = permit;
                if folder_job.validate().is_err() {
                    return (Some(false), None);
                }
                let free = nix::sys::statvfs::fstatvfs(&folder_job.root)
                    .ok()
                    .map(|fs| crate::filesystem::bytes(fs.blocks_available(), fs.fragment_size()));
                (Some(true), free)
            });
            let (identity_ok, free_bytes) = match tokio::time::timeout(self.deadline(), job).await {
                Ok(Ok(result)) => result,
                _ => (None, None),
            };
            result.push(FolderDiagnostic {
                folder_id: folder.settings.id,
                read_only: folder.settings.read_only,
                identity_ok,
                free_bytes,
                committed_bytes: bytes.cast_unsigned(),
                invalid_blocks: invalid.cast_unsigned(),
                quota_bytes: folder.settings.quota_bytes,
                minimum_free_bytes: folder.settings.min_free_bytes,
                checked_at_ms: now(),
            });
        }
        Ok(result)
    }
}
