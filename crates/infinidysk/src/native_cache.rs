//! Native-cache control plane. All routes are inside the authenticated admin router.
use crate::admin::{Admin, Fail};
use axum::Json;
use axum::extract::{Query, State};

use infinidysk_native_cache::{Coverage, Entry, Stats};
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use utoipa::{IntoParams, ToSchema};

/// Shared compatibility bridge. The original repair ledger stays immutable; cache writes are opt-in.
pub(crate) struct LegacyReader {
    db: infinidysk_db::Db,
    legacy: Arc<infinidysk_native_cache::legacy::Legacy>,
    writer: Option<Arc<infinidysk_native_cache::legacy::Writer>>,
    revisions: infinidysk_import::native::Ledger,
}
impl LegacyReader {
    pub(crate) async fn open(
        db: infinidysk_db::Db,
        settings: &infinidysk_native_cache::legacy::Settings,
    ) -> anyhow::Result<Arc<Self>> {
        let path = std::path::Path::new(&settings.metadata_path);
        let legacy = Arc::new(if settings.write.is_some() {
            infinidysk_native_cache::legacy::Legacy::open_for_writes(
                &path.join("catalogue.db"),
                &settings.folders,
            )
            .await?
        } else {
            infinidysk_native_cache::legacy::Legacy::open_in_place(
                &path.join("catalogue.db"),
                &settings.folders,
            )
            .await?
        });
        let writer = settings
            .write
            .clone()
            .map(|settings| infinidysk_native_cache::legacy::Writer::new(legacy.clone(), settings))
            .transpose()?;
        let revisions =
            infinidysk_import::native::Ledger::open_in_place(&path.join("repair-revisions.db"))
                .await?;
        Ok(Arc::new(Self {
            db,
            legacy,
            writer,
            revisions,
        }))
    }
}
impl LegacyReader {
    async fn entry(
        &self,
        key: &infinidysk_native_cache::Key,
    ) -> Result<Option<infinidysk_native_cache::legacy::Entry>, infinidysk_native_cache::Error>
    {
        if let Some(generation) = self.original_generation(key).await?
            && let Some(entry) = self
                .legacy
                .find(key.item(), &generation, key.size())
                .await?
        {
            return Ok(Some(entry));
        }
        self.legacy
            .find(
                key.item(),
                &format!("rust-v2:{}", key.revision()),
                key.size(),
            )
            .await
    }
    async fn original_generation(
        &self,
        key: &infinidysk_native_cache::Key,
    ) -> Result<Option<String>, infinidysk_native_cache::Error> {
        use infinidysk_native_cache::{Error, Key};
        let Some(raw) = self
            .db
            .import_source(infinidysk_core::ItemId(key.item()))
            .await
            .map_err(|_| Error::Invalid("cannot read legacy source provenance"))?
        else {
            return Ok(None);
        };
        let Ok(source) = serde_json::from_str::<infinidysk_import::native::Source>(&raw) else {
            return Ok(None);
        };
        let Ok(fingerprint) = <[u8; 32]>::try_from(source.fingerprint.as_slice()) else {
            return Ok(None);
        };
        if source.version != 1
            || Key::from_fingerprint(key.item(), fingerprint, source.size) != *key
        {
            return Ok(None);
        }
        source
            .generation(&self.revisions)
            .await
            .map(Some)
            .map_err(|_| Error::Invalid("cannot verify legacy repair generation"))
    }
}
impl infinidysk_native_cache::legacy::Reader for LegacyReader {
    fn catalogue(&self) -> Option<&infinidysk_native_cache::legacy::Legacy> {
        Some(&self.legacy)
    }
    fn writer(&self) -> Option<&Arc<infinidysk_native_cache::legacy::Writer>> {
        self.writer.as_ref()
    }
    fn read<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
        block: u64,
        permit: tokio::sync::OwnedSemaphorePermit,
    ) -> infinidysk_native_cache::legacy::ReadFuture<'a> {
        Box::pin(async move {
            let Some(entry) = self.entry(key).await? else {
                return Ok(None);
            };
            self.legacy.read_block(&entry, block, permit).await
        })
    }
    fn coverage<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
    ) -> infinidysk_native_cache::legacy::CoverageFuture<'a> {
        Box::pin(async move {
            let Some(entry) = self.entry(key).await? else {
                return Ok(vec![]);
            };
            self.legacy.coverage(&entry).await
        })
    }
    fn protect<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
    ) -> infinidysk_native_cache::legacy::WriteFuture<'a> {
        Box::pin(async move {
            let Some(entry) = self.entry(key).await? else {
                return Ok(false);
            };
            self.legacy.protect(&entry).await
        })
    }
    fn release<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
    ) -> infinidysk_native_cache::legacy::WriteFuture<'a> {
        Box::pin(async move {
            let Some(entry) = self.entry(key).await? else {
                return Ok(false);
            };
            self.legacy.release(&entry).await
        })
    }
    fn writable(&self) -> bool {
        self.writer.is_some()
    }
    fn written_bytes(&self) -> u64 {
        self.writer.as_ref().map_or(0, |w| w.written_bytes())
    }
    fn maintain(
        &self,
        context: infinidysk_native_cache::legacy::Maintenance,
    ) -> infinidysk_native_cache::legacy::MaintenanceFuture<'_> {
        Box::pin(async move {
            if let Some(writer) = &self.writer {
                writer.maintain(context).await?;
            }
            Ok(())
        })
    }
    fn flush_recency(&self) -> infinidysk_native_cache::legacy::MaintenanceFuture<'_> {
        Box::pin(self.legacy.flush_recency())
    }
    fn write<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
        block: u64,
        data: axum::body::Bytes,
        permit: tokio::sync::OwnedSemaphorePermit,
    ) -> infinidysk_native_cache::legacy::WriteFuture<'a> {
        Box::pin(async move {
            let Some(writer) = &self.writer else {
                return Ok(false);
            };
            let generation = self
                .original_generation(key)
                .await?
                .unwrap_or_else(|| format!("rust-v2:{}", key.revision()));
            let stored = writer
                .put(
                    key.item(),
                    generation.clone(),
                    key.size(),
                    block,
                    data,
                    permit,
                )
                .await?;
            if stored
                && block == 0
                && let Ok(Some(item)) = self.db.item(infinidysk_core::ItemId(key.item())).await
                && let Ok(Some(entry)) = self.legacy.find(key.item(), &generation, key.size()).await
                && let Err(error) = self
                    .legacy
                    .label(
                        &infinidysk_native_cache::legacy::EntryIdentity::from(&entry),
                        &item.name,
                    )
                    .await
            {
                tracing::warn!(reason=%error,"cache display label update deferred");
            }
            Ok(stored)
        })
    }
}

#[derive(Serialize, ToSchema)]
#[schema(as = NativeCacheStatus)]
pub struct Status {
    pub configured: bool,
    pub available: bool,
    pub recovery_mode: bool,
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
        recovery_mode: admin
            .vfs
            .native_cache()
            .is_some_and(|cache| cache.recovery_mode()),
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
            Err(error @ infinidysk_native_cache::Error::Invalid(_)) => {
                tracing::warn!(reason=%error, "one legacy cache block was rejected");
                report.rejected_blocks += 1;
                report.cursor.offset = offset;
            }
            Err(error) => {
                tracing::warn!(reason=%error, "legacy cache storage is unavailable; import paused");
                report.paused = true;
                break;
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

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct EvictionPage {
    pub before: Option<i64>,
    pub limit: Option<u32>,
    pub reason: Option<String>,
    pub search: Option<String>,
}
#[utoipa::path(get,operation_id="native_cache_evictions",path="/api/v1/native-cache/evictions",params(EvictionPage),responses((status=200,body=Vec<infinidysk_native_cache::Eviction>)))]
pub async fn evictions(
    State(admin): State<Arc<Admin>>,
    Query(page): Query<EvictionPage>,
) -> Result<Json<Vec<infinidysk_native_cache::Eviction>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    Ok(Json(
        cache
            .evictions_filtered(
                page.before,
                page.limit.unwrap_or(100),
                page.reason.as_deref(),
                page.search.as_deref().unwrap_or(""),
            )
            .await
            .map_err(|e| fail(&e))?,
    ))
}
#[utoipa::path(get,operation_id="native_cache_diagnostics",path="/api/v1/native-cache/diagnostics",responses((status=200,body=Vec<infinidysk_native_cache::FolderDiagnostic>)))]
pub async fn diagnostics(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<Vec<infinidysk_native_cache::FolderDiagnostic>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let result = tokio::time::timeout(std::time::Duration::from_secs(5), cache.diagnostics())
        .await
        .map_err(|_| {
            Fail::Unavailable("Cache diagnostic pass timed out; storage is uncertain".into())
        })?;
    Ok(Json(result.map_err(|e| fail(&e))?))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=NativeCacheProbeRequest)]
pub struct ProbeRequest {
    pub entry: Entry,
    #[serde(default)]
    pub after_block: u64,
    pub limit: Option<u32>,
}
#[utoipa::path(post,operation_id="native_cache_probe",path="/api/v1/native-cache/probe",request_body=ProbeRequest,responses((status=200,body=infinidysk_native_cache::Probe)))]
pub async fn probe(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<ProbeRequest>,
) -> Result<Json<infinidysk_native_cache::Probe>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let key = cache
        .resolve(&request.entry)
        .await
        .map_err(|e| fail(&e))?
        .ok_or(Fail::NotFound)?;
    let result = tokio::time::timeout(
        std::time::Duration::from_secs(10),
        cache.probe(&key, request.after_block, request.limit.unwrap_or(8)),
    )
    .await
    .map_err(|_| Fail::Unavailable("Cache probe timed out; retry the same cursor".into()))?;
    Ok(Json(result.map_err(|e| fail(&e))?))
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct RecoveryQuery {
    pub folder_id: uuid::Uuid,
    pub after: Option<String>,
}
#[derive(Deserialize, ToSchema)]
#[schema(as=NativeCacheRecoveryRequest)]
pub struct RecoveryRequest {
    pub folder_id: uuid::Uuid,
    pub source: infinidysk_native_cache::RecoveryCandidate,
    pub offset: u64,
}
#[utoipa::path(get,operation_id="native_cache_recovery_candidates",path="/api/v1/native-cache/recovery",params(RecoveryQuery),responses((status=200,body=infinidysk_native_cache::RecoveryPage)))]
pub async fn recovery_candidates(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<RecoveryQuery>,
) -> Result<Json<infinidysk_native_cache::RecoveryPage>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    Ok(Json(
        cache
            .recovery_candidates(query.folder_id, query.after.as_deref())
            .await
            .map_err(|e| fail(&e))?,
    ))
}
#[utoipa::path(post,operation_id="native_cache_recover",path="/api/v1/native-cache/recovery",request_body=RecoveryRequest,responses((status=200,body=infinidysk_native_cache::RecoveryReport)))]
pub async fn recover(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<RecoveryRequest>,
) -> Result<Json<infinidysk_native_cache::RecoveryReport>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let item = admin
        .vfs
        .db()
        .item(infinidysk_core::ItemId(request.source.item))
        .await
        .map_err(|_| Fail::Internal)?
        .ok_or(Fail::NotFound)?;
    if item.health_state == infinidysk_db::HealthState::Hidden {
        return Err(Fail::NotFound);
    }
    let key = admin.vfs.cache_key(&item).await.map_err(|_| {
        Fail::Unavailable("Current source and repair revision could not be verified".into())
    })?;
    if key.revision() != request.source.revision || key.size() != request.source.size_bytes {
        return Err(Fail::Bad(
            "Source generation changed; refresh recovery candidates".into(),
        ));
    }
    Ok(Json(
        cache
            .recover_blocks(request.folder_id, &key, request.offset)
            .await
            .map_err(|e| fail(&e))?,
    ))
}

#[cfg(test)]
mod legacy_tests;

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct LegacyBrowse {
    pub after: Option<String>,
    pub folder: Option<String>,
    pub search: Option<String>,
    pub sort: Option<infinidysk_native_cache::legacy::InventorySort>,
    pub coverage: Option<infinidysk_native_cache::legacy::InventoryFilter>,
}
#[utoipa::path(get,operation_id="legacy_cache_inventory",path="/api/v1/native-cache/legacy",params(LegacyBrowse),responses((status=200,body=infinidysk_native_cache::legacy::Inventory)))]
pub async fn legacy_inventory(
    State(admin): State<Arc<Admin>>,
    Query(q): Query<LegacyBrowse>,
) -> Result<Json<infinidysk_native_cache::legacy::Inventory>, Fail> {
    if q.search.as_ref().is_some_and(|s| s.len() > 256)
        || q.after.as_ref().is_some_and(|s| s.len() > 8192)
    {
        return Err(Fail::Bad("Cache search or cursor is too long".into()));
    }
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let reader = cache.legacy_reader().ok_or(Fail::NotFound)?;
    let legacy = reader.catalogue().ok_or(Fail::NotFound)?;
    let missing: Vec<uuid::Uuid> = legacy
        .unnamed_items()
        .await
        .map_err(|e| fail(&e))?
        .iter()
        .filter_map(|id| uuid::Uuid::parse_str(id).ok())
        .collect();
    let names: std::collections::HashMap<String, String> = admin
        .vfs
        .db()
        .item_labels(&missing)
        .await?
        .into_iter()
        .map(|(id, name)| (id.simple().to_string(), name))
        .collect();
    let mut result = legacy
        .inventory_page(
            &infinidysk_native_cache::legacy::Browse {
                after: q.after.as_deref().unwrap_or(""),
                folder: q.folder.as_deref(),
                search: q.search.as_deref().unwrap_or(""),
                limit: 50,
                sort: q.sort.unwrap_or_default(),
                coverage: q.coverage.unwrap_or_default(),
                names: Some(&names),
            },
            reader.writer().map(AsRef::as_ref),
        )
        .await
        .map_err(|e| fail(&e))?;
    let ids: Vec<uuid::Uuid> = result
        .entries
        .iter()
        .filter_map(|e| uuid::Uuid::parse_str(&e.item).ok())
        .collect();
    let labels: std::collections::HashMap<_, _> = admin
        .vfs
        .db()
        .item_labels(&ids)
        .await?
        .into_iter()
        .collect();
    for entry in &mut result.entries {
        if let Ok(id) = uuid::Uuid::parse_str(&entry.item) {
            entry.source_present = labels.contains_key(&id);
            if entry.name.as_ref().is_none_or(String::is_empty) {
                entry.name = labels.get(&id).cloned();
            }
        }
    }
    if cache.recovery_mode() {
        result.writable = false;
        for folder in &mut result.folders {
            folder.writable = false;
            folder.eviction_enabled = false;
        }
    }
    Ok(Json(result))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=LegacyCacheAction)]
pub struct LegacyAction {
    pub entry: infinidysk_native_cache::legacy::EntryIdentity,
    pub operation: Operation,
}
#[utoipa::path(post,operation_id="legacy_cache_action",path="/api/v1/native-cache/legacy",request_body=LegacyAction,responses((status=200,body=bool)))]
pub async fn legacy_action(
    State(admin): State<Arc<Admin>>,
    Json(a): Json<LegacyAction>,
) -> Result<Json<bool>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let reader = cache.legacy_reader().ok_or(Fail::NotFound)?;
    let legacy = reader.catalogue().ok_or(Fail::NotFound)?;
    if cache.recovery_mode()
        || !reader
            .writer()
            .is_some_and(|w| w.settings().folders.iter().any(|f| f.id == a.entry.folder))
    {
        return Ok(Json(false));
    }
    let changed = match a.operation {
        Operation::Pin => legacy.manual_pin(&a.entry, true).await,
        Operation::Unpin => legacy.manual_pin(&a.entry, false).await,
        Operation::Evict => {
            reader
                .writer()
                .ok_or_else(unavailable)?
                .manual_evict(a.entry, cache.legacy_maintenance())
                .await
        }
    }
    .map_err(|e| fail(&e))?;
    Ok(Json(changed))
}
#[utoipa::path(post,operation_id="legacy_cache_coverage",path="/api/v1/native-cache/legacy/coverage",request_body=infinidysk_native_cache::legacy::EntryIdentity,responses((status=200,body=Vec<Coverage>)))]
pub async fn legacy_coverage(
    State(admin): State<Arc<Admin>>,
    Json(id): Json<infinidysk_native_cache::legacy::EntryIdentity>,
) -> Result<Json<Vec<Coverage>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let legacy = cache
        .legacy_reader()
        .and_then(infinidysk_native_cache::legacy::Reader::catalogue)
        .ok_or(Fail::NotFound)?;
    let entry = legacy
        .exact_entry(&id)
        .await
        .map_err(|e| fail(&e))?
        .ok_or(Fail::NotFound)?;
    Ok(Json(legacy.coverage(&entry).await.map_err(|e| fail(&e))?))
}

#[utoipa::path(get,operation_id="legacy_cache_diagnostics",path="/api/v1/native-cache/legacy/diagnostics",responses((status=200,body=Vec<infinidysk_native_cache::legacy::StorageDiagnostic>)))]
pub async fn legacy_diagnostics(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<Vec<infinidysk_native_cache::legacy::StorageDiagnostic>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let legacy = cache
        .legacy_reader()
        .and_then(infinidysk_native_cache::legacy::Reader::catalogue)
        .ok_or(Fail::NotFound)?;
    Ok(Json(legacy.diagnostics().await))
}

#[derive(Deserialize, ToSchema)]
#[schema(as=LegacyCacheProbe)]
pub struct LegacyProbe {
    pub entry: infinidysk_native_cache::legacy::EntryIdentity,
    pub offset: u64,
}
#[utoipa::path(post,operation_id="legacy_cache_probe",path="/api/v1/native-cache/legacy/probe",request_body=LegacyProbe,responses((status=200,body=Option<u64>)))]
pub async fn legacy_probe(
    State(admin): State<Arc<Admin>>,
    Json(probe): Json<LegacyProbe>,
) -> Result<Json<Option<u64>>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let legacy = cache
        .legacy_reader()
        .and_then(infinidysk_native_cache::legacy::Reader::catalogue)
        .ok_or(Fail::NotFound)?;
    let bytes = tokio::time::timeout(
        std::time::Duration::from_secs(5),
        legacy.probe(&probe.entry, probe.offset),
    )
    .await
    .map_err(|_| Fail::Unavailable("Cache sample timed out; storage remains uncertain".into()))?
    .map_err(|e| fail(&e))?;
    Ok(Json(bytes))
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct LegacyEvictionQuery {
    pub before: Option<i64>,
    pub search: Option<String>,
    pub reason: Option<String>,
    pub folder: Option<String>,
}
#[utoipa::path(get,operation_id="legacy_cache_evictions",path="/api/v1/native-cache/legacy/evictions",params(LegacyEvictionQuery),responses((status=200,body=infinidysk_native_cache::legacy::OriginalEvictions)))]
pub async fn legacy_evictions(
    State(admin): State<Arc<Admin>>,
    Query(q): Query<LegacyEvictionQuery>,
) -> Result<Json<infinidysk_native_cache::legacy::OriginalEvictions>, Fail> {
    let cache = admin.vfs.native_cache().ok_or_else(unavailable)?;
    let legacy = cache
        .legacy_reader()
        .and_then(infinidysk_native_cache::legacy::Reader::catalogue)
        .ok_or(Fail::NotFound)?;
    Ok(Json(
        legacy
            .original_evictions(
                q.before,
                q.search.as_deref().unwrap_or(""),
                q.reason.as_deref(),
                q.folder.as_deref(),
                50,
            )
            .await
            .map_err(|e| fail(&e))?,
    ))
}
