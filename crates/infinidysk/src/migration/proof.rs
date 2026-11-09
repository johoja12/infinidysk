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

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
pub struct ReimportRequest {
    pub source: Request,
    pub current_item: Uuid,
    pub submission_intent: String,
    pub source_release_id: String,
}
#[derive(Serialize, ToSchema)]
pub struct ReimportProof {
    #[serde(flatten)]
    pub member: Proof,
    pub submission_intent: String,
    pub job_id: Uuid,
    pub download_dir: Uuid,
}
#[utoipa::path(post,operation_id="migration_reimport_proof",path="/api/v1/migration/reimport-proof",request_body=ReimportRequest,responses((status=200,body=ReimportProof),(status=409,description="Import or exact member identity differs")))]
pub async fn capture_reimport(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<ReimportRequest>,
) -> Result<Json<ReimportProof>, Fail> {
    let source = &request.source;
    if !FsPath::new(&source.source_config).is_absolute()
        || source.source_config.len() > 4096
        || source.source_config.chars().any(char::is_control)
        || !sha(&source.package_digest)
        || !sha(&source.identity_digest)
        || !sha(&source.payload_sha256)
        || !sha(&request.submission_intent)
        || !matches!(
            source.identity_kind.as_str(),
            "direct-articles-v1" | "archive-articles-v2"
        )
    {
        return Err(Fail::Bad(
            "Exact offline member and durable submission identities are required".into(),
        ));
    }
    let expected_intent = infinidysk_queue::service::migration_intent(
        &source.package_digest,
        &request.source_release_id,
    )
    .map_err(|_| Fail::Bad("Invalid source release binding".into()))?;
    if expected_intent != request.submission_intent {
        return Err(Fail::Bad(
            "Submission does not bind this sealed package and release".into(),
        ));
    }
    let permit = LANE
        .try_acquire()
        .map_err(|_| Fail::Unavailable("Offline mapping is busy; retry later".into()))?;
    let task = tokio::spawn(async move {
        let _permit = permit;
        prove_reimport(&admin, request).await
    });
    Ok(Json(
        tokio::time::timeout(Duration::from_secs(30), task)
            .await
            .map_err(|_| Fail::Unavailable("Offline mapping is still running; retry later".into()))?
            .map_err(|_| Fail::Internal)??,
    ))
}
async fn completed_import(
    admin: &Admin,
    intent: &str,
    payload: &str,
    current: ItemId,
) -> Result<(Uuid, ItemId), Fail> {
    let db = admin.vfs.db();
    let receipt = db.migration_submission(intent).await?.ok_or_else(|| {
        Fail::Conflict("Submission acceptance is unavailable; replay is not authorized".into())
    })?;
    if receipt.payload_sha256 != payload
        || db
            .queue_contains(infinidysk_core::JobId(receipt.job_id))
            .await?
    {
        return Err(Fail::Conflict(
            "This exact source has not completed importing".into(),
        ));
    }
    let history = db
        .history_entry(infinidysk_core::JobId(receipt.job_id))
        .await?
        .ok_or_else(|| {
            Fail::Conflict("Import history is unavailable; member validation stays pending".into())
        })?;
    if history.status != infinidysk_db::JobStatus::Completed {
        return Err(Fail::Conflict(
            "Import did not complete successfully".into(),
        ));
    }
    let root = history
        .download_dir
        .ok_or_else(|| Fail::Conflict("Import has no source directory".into()))?;
    if !db.item_descends_from(current, root).await? {
        return Err(Fail::Conflict(
            "Member does not belong to the accepted import".into(),
        ));
    }
    Ok((receipt.job_id, root))
}
async fn prove_reimport(admin: &Admin, request: ReimportRequest) -> Result<ReimportProof, Fail> {
    let db = admin.vfs.db();
    let id = ItemId(request.current_item);
    let source = request.source;
    let (job, root) = completed_import(
        admin,
        &request.submission_intent,
        &source.payload_sha256,
        id,
    )
    .await?;
    let expected = infinidysk_import::capture_reimport_identity(
        FsPath::new(&source.source_config),
        db,
        ItemId(source.legacy_item),
        id,
    )
    .await
    .map_err(|error| match error {
        infinidysk_import::Error::MemberMismatch => {
            Fail::Conflict("Current candidate differs from the exact legacy member".into())
        }
        _ => Fail::Unavailable("Offline source cannot prove this exact reimported member".into()),
    })?;
    let evidence = expected.evidence;
    if evidence.version != 1
        || evidence.kind != source.identity_kind
        || evidence.digest != source.identity_digest
        || evidence.payload_sha256 != source.payload_sha256
        || evidence.size != source.size
    {
        return Err(Fail::Conflict(
            "Reimport differs from the sealed legacy member".into(),
        ));
    }
    let item = db.item(id).await?.ok_or(Fail::NotFound)?;
    if item.health_state == HealthState::Hidden || item.size != Some(source.size) {
        return Err(Fail::Conflict(
            "Reimported member was removed or replaced".into(),
        ));
    }
    let _reader = admin
        .vfs
        .open(
            &Node::Item(item.clone()),
            Origin {
                delivery: Delivery::WebDav,
                client: "migration-reimport-validation".into(),
                player: None,
            },
        )
        .await
        .map_err(|_| Fail::Unavailable("Current member cannot be held for validation".into()))?
        .ok_or(Fail::NotFound)?;
    let meta = item.meta_blob.ok_or(Fail::NotFound)?;
    let raw = db
        .blob_limited(meta, 268_435_456)
        .await?
        .ok_or(Fail::NotFound)?;
    let (raw, fingerprint) = tokio::task::spawn_blocking(move || {
        let decoded = blob::decode_meta(&raw)?;
        let fingerprint = Sha256::digest(decoded.content_identity()?).to_vec();
        Ok::<_, infinidysk_core::blob::Error>((raw, fingerprint))
    })
    .await
    .map_err(|_| Fail::Internal)?
    .map_err(|_| Fail::Unavailable("Current member metadata cannot be verified".into()))?;
    if fingerprint != expected.fingerprint || !db.metadata_is_current(id, meta, &raw).await? {
        return Err(Fail::Conflict(
            "Current member changed during mapping".into(),
        ));
    }
    let key = admin
        .vfs
        .cache_key(&item)
        .await
        .map_err(|_| Fail::Unavailable("Current member revision is unavailable".into()))?;
    let current = db.item(id).await?.ok_or(Fail::NotFound)?;
    if current.meta_blob != Some(meta)
        || current.size != Some(source.size)
        || current.health_state == HealthState::Hidden
        || !db.metadata_is_current(id, meta, &raw).await?
        || completed_import(
            admin,
            &request.submission_intent,
            &source.payload_sha256,
            id,
        )
        .await?
            != (job, root)
    {
        return Err(Fail::Conflict(
            "Import or source changed during mapping".into(),
        ));
    }
    let record = serde_json::to_vec(&(
        1_u8,
        &source.package_digest,
        source.legacy_item,
        request.current_item,
        &request.submission_intent,
        job,
        root.0,
        key.revision(),
        &evidence,
    ))
    .map_err(|_| Fail::Internal)?;
    Ok(ReimportProof {
        member: Proof {
            version: 1,
            package_digest: source.package_digest,
            legacy_item: source.legacy_item,
            current_item: request.current_item,
            identity_kind: evidence.kind,
            identity_digest: evidence.digest,
            payload_sha256: evidence.payload_sha256,
            size: source.size,
            source_revision: key.revision().into(),
            ids_path: format!("/{}", id.ids_path()),
            observed_at: infinidysk_db::now_ms(),
            record_digest: crate::migration::digest_bytes(&record),
            payload_validated: false,
        },
        submission_intent: request.submission_intent,
        job_id: job,
        download_dir: root.0,
    })
}
