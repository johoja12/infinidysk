//! Native-cache control plane. All routes are inside the authenticated admin router.
use crate::admin::{Admin, Fail};
use axum::Json;
use axum::extract::{Query, State};

use infinidysk_native_cache::{Coverage, Entry, Stats};
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use utoipa::{IntoParams, ToSchema};

#[derive(Serialize, ToSchema)]
#[schema(as = NativeCacheStatus)]
pub struct Status {
    pub configured: bool,
    pub available: bool,
    pub reason: Option<String>,
    pub stats: Option<Stats>,
}
fn fail(error: &infinidysk_native_cache::Error) -> Fail {
    tracing::warn!(reason=%error,"native cache operation failed");
    Fail::Unavailable("Native cache operation failed; check storage status".to_owned())
}
#[utoipa::path(get,operation_id="native_cache_status",path="/api/v1/native-cache",responses((status=200,body=Status)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Result<Json<Status>, Fail> {
    let stats = match admin.vfs.native_cache() {
        Some(cache) => Some(cache.stats().await.map_err(|e| fail(&e))?),
        None => None,
    };
    Ok(Json(Status {
        configured: admin.config.read().await.settings().native_cache.enabled,
        available: stats.is_some(),
        reason: admin.live.native_cache_error().map(str::to_owned),
        stats,
    }))
}
#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct Page {
    pub offset: Option<u32>,
    pub limit: Option<u32>,
}
#[utoipa::path(get,operation_id="native_cache_files",path="/api/v1/native-cache/files",params(Page),responses((status=200,body=Vec<Entry>)))]
pub async fn files(
    State(admin): State<Arc<Admin>>,
    Query(page): Query<Page>,
) -> Result<Json<Vec<Entry>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    Ok(Json(
        cache
            .entries(page.offset.unwrap_or(0), page.limit.unwrap_or(100))
            .await
            .map_err(|e| fail(&e))?,
    ))
}
#[derive(Deserialize, ToSchema)]
#[schema(as = NativeCacheAction)]
pub struct Action {
    pub entry: Entry,
    pub operation: Operation,
}
#[derive(Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as = NativeCacheOperation)]
pub enum Operation {
    Pin,
    Unpin,
    Evict,
}
#[utoipa::path(post,operation_id="native_cache_action",path="/api/v1/native-cache/files",request_body=Action,responses((status=200,body=bool)))]
pub async fn action(
    State(admin): State<Arc<Admin>>,
    Json(action): Json<Action>,
) -> Result<Json<bool>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let key = cache
        .resolve(&action.entry)
        .await
        .map_err(|e| fail(&e))?
        .ok_or(Fail::NotFound)?;
    Ok(Json(match action.operation {
        Operation::Pin => cache.pin(&key, true).await.map_err(|e| fail(&e))?,
        Operation::Unpin => cache.pin(&key, false).await.map_err(|e| fail(&e))?,
        Operation::Evict => cache.evict(&key, "manual").await.map_err(|e| fail(&e))?,
    }))
}
#[utoipa::path(post,operation_id="native_cache_coverage",path="/api/v1/native-cache/coverage",request_body=Entry,responses((status=200,body=Vec<Coverage>)))]
pub async fn coverage(
    State(admin): State<Arc<Admin>>,
    Json(entry): Json<Entry>,
) -> Result<Json<Vec<Coverage>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let key = cache
        .resolve(&entry)
        .await
        .map_err(|e| fail(&e))?
        .ok_or(Fail::NotFound)?;
    Ok(Json(cache.coverage(&key).await.map_err(|e| fail(&e))?))
}
fn unavailable() -> Fail {
    Fail::Unavailable("Native cache is unavailable".to_owned())
}

#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheCursor)]
pub struct LegacyCursor {
    /// Last fully processed entry. The next entry's last processed block is `offset`.
    #[serde(default)]
    pub after_key: String,
    #[serde(default = "before_first")]
    pub offset: i64,
}
const fn before_first() -> i64 {
    -1
}
impl Default for LegacyCursor {
    fn default() -> Self {
        Self {
            after_key: String::new(),
            offset: -1,
        }
    }
}
#[derive(Clone, Deserialize, ToSchema)]
#[schema(as = LegacyCacheImport)]
pub struct LegacyImport {
    /// Offline/checkpointed 1.x native metadata directory, containing both ledgers.
    pub metadata_path: String,
    pub folders: Vec<infinidysk_native_cache::legacy::Folder>,
    #[serde(default)]
    pub cursor: LegacyCursor,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheReport)]
pub struct LegacyReport {
    pub cursor: LegacyCursor,
    pub complete: bool,
    pub processed_bytes: u64,
    pub rejected_entries: u32,
    pub rejected_blocks: u32,
    pub paused: bool,
}
#[utoipa::path(post,operation_id="native_cache_import_legacy",path="/api/v1/native-cache/import-legacy",request_body=LegacyImport,responses((status=200,body=LegacyReport)))]
pub async fn import_legacy(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<LegacyImport>,
) -> Result<Json<LegacyReport>, Fail> {
    use infinidysk_native_cache::legacy::Legacy;
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let path = std::path::Path::new(&request.metadata_path);
    if !path.is_absolute() || request.cursor.offset < -1 || request.cursor.after_key.len() > 64 {
        return Err(Fail::Unavailable(
            "Legacy import needs an absolute metadata path and valid cursor".into(),
        ));
    }
    let legacy = Legacy::open(&path.join("catalogue.db"), &request.folders)
        .await
        .map_err(|e| fail(&e))?;
    let revisions = infinidysk_import::native::Ledger::open(&path.join("repair-revisions.db"))
        .await
        .map_err(|_| Fail::Unavailable("Cannot read the original repair revision ledger".into()))?;
    let mut report = LegacyReport {
        cursor: request.cursor,
        complete: false,
        processed_bytes: 0,
        rejected_entries: 0,
        rejected_blocks: 0,
        paused: false,
    };
    // Bounded turns keep foreground playback and cancellation responsive. Repeating a
    // cursor is safe: complete blocks already published under the exact key are reused.
    let mut mapped: Option<(String, Option<infinidysk_native_cache::Key>)> = None;
    for _ in 0..16 {
        let Some(entry) = legacy
            .entries(&report.cursor.after_key, 1)
            .await
            .map_err(|e| fail(&e))?
            .pop()
        else {
            report.complete = true;
            break;
        };
        let mapping = if let Some((id, key)) = &mapped
            && id == &entry.key
        {
            key.clone()
        } else {
            let key = legacy_mapping(&admin, &entry, &revisions).await?;
            mapped = Some((entry.key.clone(), key.clone()));
            key
        };
        let Some(key) = mapping else {
            report.rejected_entries += 1;
            report.cursor = LegacyCursor {
                after_key: entry.key,
                offset: -1,
            };
            continue;
        };
        let blocks = legacy
            .blocks(&entry, report.cursor.offset, 1)
            .await
            .map_err(|e| fail(&e))?;
        let Some(block) = blocks.into_iter().next() else {
            if entry.pinned != 0 {
                cache.pin(&key, true).await.map_err(|e| fail(&e))?;
            }
            report.cursor = LegacyCursor {
                after_key: entry.key,
                offset: -1,
            };
            continue;
        };
        let offset = block.offset;
        let count = u64::try_from(block.count).unwrap_or(0);
        match legacy.copy_block(cache, &entry, &key, block).await {
            Ok(true) => {
                report.processed_bytes += count;
                report.cursor.offset = offset;
            }
            Ok(false) => {
                report.paused = true;
                break;
            }
            Err(error) => {
                tracing::warn!(reason=%error, "one legacy cache block was rejected");
                report.rejected_blocks += 1;
                report.cursor.offset = offset;
            }
        }
    }
    Ok(Json(report))
}
async fn legacy_mapping(
    admin: &Admin,
    entry: &infinidysk_native_cache::legacy::Entry,
    revisions: &infinidysk_import::native::Ledger,
) -> Result<Option<infinidysk_native_cache::Key>, Fail> {
    let Ok(uuid) = uuid::Uuid::parse_str(&entry.item) else {
        return Ok(None);
    };
    let item = infinidysk_core::ItemId(uuid);
    let db = admin.queue.db();
    let Some(source) = db.import_source(item).await? else {
        return Ok(None);
    };
    let Ok(source) = serde_json::from_str::<infinidysk_import::native::Source>(&source) else {
        return Ok(None);
    };
    if source.version != 1 {
        return Ok(None);
    }
    let Some(item) = db.item(item).await? else {
        return Ok(None);
    };
    let key = admin
        .vfs
        .cache_key(&item)
        .await
        .map_err(|_| Fail::Unavailable("Cannot verify imported source metadata".into()))?;
    let Ok(fingerprint) = <[u8; 32]>::try_from(source.fingerprint.as_slice()) else {
        return Ok(None);
    };
    let original = infinidysk_native_cache::Key::from_fingerprint(uuid, fingerprint, source.size);
    if original != key {
        return Ok(None);
    }
    let generation = source.generation(revisions).await.map_err(|_| {
        Fail::Unavailable("Cannot verify the original repair revision ledger".into())
    })?;
    Ok(
        infinidysk_native_cache::legacy::Legacy::matches(entry, uuid, &generation, source.size)
            .then_some(key),
    )
}
