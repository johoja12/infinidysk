//! Offline legacy-member identity. This is not a media-health or playback certificate.
use crate::admin::{Admin, Fail};
use axum::{Json, extract::State};
use infinidysk_core::{ItemId, blob};
use infinidysk_db::HealthState;
use infinidysk_vfs::{Delivery, Node, Origin};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use std::path::Path as FsPath;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::Semaphore;
use utoipa::ToSchema;
use uuid::Uuid;

static LANE: Semaphore = Semaphore::const_new(1);
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
pub struct Request {
    pub source_config: String,
    pub package_digest: String,
    pub legacy_item: Uuid,
    pub identity_kind: String,
    pub identity_digest: String,
    pub payload_sha256: String,
    pub size: u64,
}
#[derive(Serialize, ToSchema)]
pub struct Proof {
    pub version: u8,
    pub package_digest: String,
    pub legacy_item: Uuid,
    pub current_item: Uuid,
    pub identity_kind: String,
    pub identity_digest: String,
    pub payload_sha256: String,
    pub size: u64,
    pub source_revision: String,
    pub ids_path: String,
    pub observed_at: i64,
    pub record_digest: String,
    /// Exact metadata/article identity does not establish healthy media payloads.
    pub payload_validated: bool,
}
fn sha(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
#[utoipa::path(post,operation_id="migration_legacy_proof",path="/api/v1/migration/legacy-proof",request_body=Request,responses((status=200,body=Proof),(status=409,description="Source identity changed"),(status=503,description="Offline source unavailable or mapping lane busy")))]
pub async fn capture(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<Request>,
) -> Result<Json<Proof>, Fail> {
    if !FsPath::new(&request.source_config).is_absolute()
        || request.source_config.len() > 4096
        || request.source_config.chars().any(char::is_control)
        || !sha(&request.package_digest)
        || !sha(&request.identity_digest)
        || !sha(&request.payload_sha256)
        || !matches!(
            request.identity_kind.as_str(),
            "direct-articles-v1" | "archive-articles-v2"
        )
    {
        return Err(Fail::Bad(
            "An absolute offline source and exact supported package/member identity are required"
                .into(),
        ));
    }
    let permit = LANE.try_acquire().map_err(|_| {
        Fail::Unavailable("Offline mapping is busy; retry this member later".into())
    })?;
    // A disconnected/timed-out HTTP caller cannot release the admission while a
    // blocking source parse still runs. The owned task finishes or retains its slot.
    let task = tokio::spawn(async move {
        let _permit = permit;
        prove(&admin, request).await
    });
    let proof = tokio::time::timeout(Duration::from_secs(30), task)
        .await
        .map_err(|_| {
            Fail::Unavailable("Offline mapping is still running; retry this member later".into())
        })?
        .map_err(|_| Fail::Internal)??;
    Ok(Json(proof))
}
async fn prove(admin: &Admin, request: Request) -> Result<Proof, Fail> {
    let db = admin.vfs.db();
    let id = ItemId(request.legacy_item);
    let evidence =
        infinidysk_import::capture_mapped_source(FsPath::new(&request.source_config), db, id)
            .await
            .map_err(|_| {
                Fail::Unavailable(
            "Offline source cannot prove this imported generation; original evidence was retained"
                .into(),
        )
            })?;
    if evidence.version != 1
        || evidence.kind != request.identity_kind
        || evidence.digest != request.identity_digest
        || evidence.payload_sha256 != request.payload_sha256
        || evidence.size != request.size
    {
        return Err(Fail::Conflict(
            "The sealed source member does not match this imported identity".into(),
        ));
    }
    let item = db.item(id).await?.ok_or(Fail::NotFound)?;
    if item.health_state == HealthState::Hidden || item.size != Some(request.size) {
        return Err(Fail::Conflict("Source was removed or replaced".into()));
    }
    // The read handle protects source/repair publication while its metadata and
    // revision are fenced. It is never polled for media bytes.
    let _reader = admin
        .vfs
        .open(
            &Node::Item(item.clone()),
            Origin {
                delivery: Delivery::WebDav,
                client: "migration-validation".into(),
                player: None,
            },
        )
        .await
        .map_err(|_| Fail::Unavailable("Current source cannot be held for validation".into()))?
        .ok_or(Fail::NotFound)?;
    let meta = item.meta_blob.ok_or(Fail::NotFound)?;
    let source = db.import_source(id).await?.ok_or(Fail::NotFound)?;
    let source: infinidysk_import::native::Source = serde_json::from_str(&source)
        .map_err(|_| Fail::Unavailable("Imported provenance is unreadable".into()))?;
    let raw = db
        .blob_limited(meta, 268_435_456)
        .await?
        .ok_or(Fail::NotFound)?;
    let (raw, fingerprint) = tokio::task::spawn_blocking(move || {
        let metadata = blob::decode_meta(&raw)?;
        let fingerprint = Sha256::digest(metadata.content_identity()?).to_vec();
        Ok::<_, infinidysk_core::blob::Error>((raw, fingerprint))
    })
    .await
    .map_err(|_| Fail::Internal)?
    .map_err(|_| Fail::Unavailable("Current source metadata cannot be verified".into()))?;
    if source.version != 1
        || source.fingerprint != fingerprint
        || source.size != request.size
        || source.mapped_identity.as_ref() != Some(&evidence)
    {
        return Err(Fail::Conflict(
            "Imported source changed during member validation".into(),
        ));
    }
    let key = admin
        .vfs
        .cache_key(&item)
        .await
        .map_err(|_| Fail::Unavailable("Current source revision is unavailable".into()))?;
    if !db.metadata_is_current(id, meta, &raw).await? {
        return Err(Fail::Conflict(
            "Source metadata changed during member validation".into(),
        ));
    }
    let current = db.item(id).await?.ok_or(Fail::NotFound)?;
    if current.meta_blob != Some(meta)
        || current.size != Some(request.size)
        || current.health_state == HealthState::Hidden
    {
        return Err(Fail::Conflict(
            "Source changed during member validation".into(),
        ));
    }
    let record = serde_json::to_vec(&(
        1_u8,
        &request.package_digest,
        request.legacy_item,
        key.revision(),
        request.size,
        &evidence.kind,
        &evidence.digest,
        &evidence.payload_sha256,
    ))
    .map_err(|_| Fail::Internal)?;
    let record_digest = crate::migration::digest_bytes(&record);
    Ok(Proof {
        version: 1,
        package_digest: request.package_digest,
        legacy_item: request.legacy_item,
        current_item: request.legacy_item,
        identity_kind: evidence.kind,
        identity_digest: evidence.digest,
        payload_sha256: evidence.payload_sha256,
        size: request.size,
        source_revision: key.revision().into(),
        ids_path: format!("/{}", id.ids_path()),
        observed_at: infinidysk_db::now_ms(),
        record_digest,
        payload_validated: false,
    })
}
