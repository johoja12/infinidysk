//! Operational migration admission and readback. Acceptance proves a queued NZB,
//! never source-member correlation or permission to promote a library link.
pub(crate) mod proof;
use crate::admin::{Admin, Fail};
use axum::{
    Json,
    body::Bytes,
    extract::{Path, State},
    http::HeaderMap,
};
use base64::Engine as _;
use infinidysk_core::JobId;
use infinidysk_db::{JobStatus, MigrationSubmission};
use infinidysk_queue::Submission;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use utoipa::ToSchema;
use uuid::Uuid;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Parameters {
    pub package_digest: String,
    pub source_release_id: String,
    pub file_name: String,
    pub job_name: Option<String>,
    pub category: String,
    pub priority: i16,
    pub post_processing: i16,
}
#[derive(Serialize, ToSchema)]
pub struct Receipt {
    pub intent: String,
    pub scope: String,
    pub payload_sha256: String,
    pub job_id: Uuid,
    pub accepted_at: i64,
    /// Missing history remains unknown; it does not authorize replay.
    pub state: String,
    pub download_dir: Option<Uuid>,
}
async fn view(admin: &Admin, receipt: MigrationSubmission) -> Result<Receipt, Fail> {
    let db = admin.queue.db();
    let job = JobId(receipt.job_id);
    let mut dir = None;
    let state = if db.queue_contains(job).await? {
        "queued"
    } else if let Some(history) = db.history_entry(job).await? {
        match history.status {
            JobStatus::Completed => {
                if let Some(id) = history.download_dir
                    && db.item(id).await?.is_some()
                {
                    dir = Some(id.0);
                    "completed"
                } else {
                    "completed_source_missing"
                }
            }
            JobStatus::Failed => "failed",
        }
    } else {
        "accepted_history_unavailable"
    };
    Ok(Receipt {
        intent: receipt.intent,
        scope: receipt.scope,
        payload_sha256: receipt.payload_sha256,
        job_id: receipt.job_id,
        accepted_at: receipt.created_at,
        state: state.into(),
        download_dir: dir,
    })
}
#[utoipa::path(post,operation_id="migration_submit",path="/api/v1/migration/submissions",params(("x-migration-options"=String,Header,description="Base64 JSON with package_digest, source_release_id, file_name, optional job_name, category, priority and post_processing. Kept out of URL/access logs.")),request_body(content=String,content_type="application/x-nzb"),responses((status=200,body=Receipt),(status=409,description="Intent/content conflict")))]
pub async fn submit(
    State(admin): State<Arc<Admin>>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Json<Receipt>, Fail> {
    let encoded = headers
        .get("x-migration-options")
        .and_then(|v| v.to_str().ok())
        .filter(|v| v.len() <= 8192)
        .ok_or_else(|| Fail::Bad("A bounded x-migration-options header is required".into()))?;
    let raw = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .map_err(|_| Fail::Bad("Invalid migration options".into()))?;
    let input: Parameters =
        serde_json::from_slice(&raw).map_err(|_| Fail::Bad("Invalid migration options".into()))?;
    let job = Submission {
        file_name: &input.file_name,
        category: &input.category,
        priority: input.priority,
        post_processing: input.post_processing,
    };
    let receipt = admin
        .queue
        .enqueue_migration_named(
            &job,
            &input.package_digest,
            &input.source_release_id,
            input.job_name.as_deref(),
            &body,
        )
        .await
        .map_err(|error| match error {
            infinidysk_queue::service::Error::Migration(reason) => Fail::Conflict(reason.into()),
            infinidysk_queue::service::Error::Nzb(_) => {
                Fail::Bad("Migration NZB is invalid; no submission was accepted".into())
            }
            other => Fail::from(other),
        })?;
    Ok(Json(view(&admin, receipt).await?))
}
#[utoipa::path(get,operation_id="migration_submission",path="/api/v1/migration/submissions/{intent}",params(("intent"=String,Path)),responses((status=200,body=Option<Receipt>)))]
pub async fn status(
    State(admin): State<Arc<Admin>>,
    Path(intent): Path<String>,
) -> Result<Json<Option<Receipt>>, Fail> {
    if intent.len() != 64
        || !intent
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return Err(Fail::Bad("Invalid migration intent".into()));
    }
    let Some(receipt) = admin.queue.db().migration_submission(&intent).await? else {
        return Ok(Json(None));
    };
    Ok(Json(Some(view(&admin, receipt).await?)))
}

fn digest_bytes(bytes: &[u8]) -> String {
    use sha2::{Digest as _, Sha256};
    use std::fmt::Write as _;
    Sha256::digest(bytes)
        .iter()
        .fold(String::with_capacity(64), |mut out, b| {
            write!(out, "{b:02x}").expect("formatting into String cannot fail");
            out
        })
}

#[derive(Serialize, ToSchema)]
pub struct MountProof {
    pub ids_path: String,
    pub size: u64,
    pub sha256: String,
    pub expires_at: i64,
}
#[utoipa::path(post,operation_id="migration_mount_probe",path="/api/v1/migration/mount-probe",responses((status=200,body=MountProof)))]
pub async fn mount_probe(State(admin): State<Arc<Admin>>) -> Json<MountProof> {
    let probe = admin.vfs.mount_probe();
    Json(MountProof {
        ids_path: format!("/{}", probe.id().ids_path()),
        size: probe.data().len() as u64,
        sha256: digest_bytes(probe.data()),
        expires_at: probe.expires_at_ms(),
    })
}
