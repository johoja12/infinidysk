//! Authenticated manual warming controls. Automatic Plex planning is a separate stage.
use crate::admin::{Admin, Fail};
use axum::{
    Json,
    extract::{Path, State},
};
use infinidysk_core::ItemId;
use infinidysk_prefetch::{Job, Operation, Range, Status};
use serde::{Deserialize, Serialize};
mod followups;
pub use followups::Followups;
use std::sync::Arc;
use utoipa::ToSchema;
use uuid::Uuid;
fn fail(error: &anyhow::Error) -> Fail {
    tracing::warn!(reason=%error,"prefetch operation failed");
    Fail::Bad(
        "Warming operation could not be applied; check the job, source and native cache status"
            .to_owned(),
    )
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PrefetchRequest)]
pub struct Request {
    pub item: Uuid,
    #[serde(default)]
    pub ranges: Vec<Range>,
    pub priority: u8,
    pub intent: Uuid,
}
#[derive(Serialize, ToSchema)]
#[schema(as=PrefetchView)]
pub struct View {
    #[serde(flatten)]
    pub queue: Status,
    pub followup: followups::Snapshot,
    pub names: std::collections::HashMap<Uuid, String>,
}
#[utoipa::path(get,operation_id="prefetch_status",path="/api/v1/prefetch",responses((status=200,body=View)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Json<View> {
    let queue = admin.prefetch.status().await;
    let jobs: Vec<Job> = queue
        .jobs
        .iter()
        .cloned()
        .chain(queue.history.iter().map(|h| h.job.clone()))
        .collect();
    let mut ids: Vec<Uuid> = jobs.iter().map(|j| j.item).collect();
    ids.sort_unstable();
    ids.dedup();
    let names = admin
        .vfs
        .db()
        .item_labels(&ids)
        .await
        .unwrap_or_default()
        .into_iter()
        .collect();
    let followup = admin
        .prefetch_followups
        .snapshot(Arc::clone(&admin), jobs)
        .await;
    Json(View {
        queue,
        followup,
        names,
    })
}
#[utoipa::path(post,operation_id="prefetch_enqueue",path="/api/v1/prefetch",request_body=Request,responses((status=200,body=Job)))]
pub async fn enqueue(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<Request>,
) -> Result<Json<Job>, Fail> {
    let ranges = if request.ranges.is_empty() {
        let item = admin
            .vfs
            .db()
            .item(ItemId(request.item))
            .await?
            .ok_or(Fail::NotFound)?;
        vec![Range {
            start: 0,
            end: item.size.unwrap_or(0),
        }]
    } else {
        request.ranges
    };
    Ok(Json(
        admin
            .prefetch
            .enqueue(
                ItemId(request.item),
                &ranges,
                request.priority,
                request.intent,
            )
            .await
            .map_err(|e| fail(&e))?,
    ))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PrefetchPause)]
pub struct Pause {
    pub paused: bool,
}
#[utoipa::path(post,operation_id="prefetch_pause",path="/api/v1/prefetch/pause",request_body=Pause,responses((status=200)))]
pub async fn pause(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<Pause>,
) -> Result<(), Fail> {
    admin
        .prefetch
        .pause(request.paused)
        .await
        .map_err(|e| fail(&e))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PrefetchControl)]
pub struct Control {
    pub operation: Operation,
}
#[utoipa::path(post,operation_id="prefetch_control",path="/api/v1/prefetch/{id}",params(("id"=Uuid,Path)),request_body=Control,responses((status=200)))]
pub async fn control(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(request): Json<Control>,
) -> Result<(), Fail> {
    admin
        .prefetch
        .control(id, request.operation)
        .await
        .map_err(|e| fail(&e))
}
