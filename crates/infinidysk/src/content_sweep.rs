//! Authenticated resumable content sampling controls.
use crate::admin::{Admin, Fail};
use axum::{Json, extract::State};
use infinidysk_health::sweep::{Control, View};
use serde::Deserialize;
use std::sync::Arc;
use utoipa::ToSchema;
#[derive(Deserialize, ToSchema)]
#[schema(as=ContentSweepRequest)]
pub struct Request {
    pub revision: i64,
    #[serde(flatten)]
    pub control: Control,
}
#[utoipa::path(get,operation_id="content_sweep",path="/api/v1/health/content-sweep",responses((status=200,body=View)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Result<Json<View>, Fail> {
    Ok(Json(
        admin
            .health
            .content_sweep()
            .await
            .map_err(|e| Fail::Bad(e.to_string()))?,
    ))
}
#[utoipa::path(post,operation_id="content_sweep_control",path="/api/v1/health/content-sweep",request_body=Request,responses((status=200,body=View)))]
pub async fn control(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<Request>,
) -> Result<Json<View>, Fail> {
    if !admin
        .health
        .control_sweep(request.revision, request.control)
        .await
        .map_err(|e| Fail::Bad(e.to_string()))?
    {
        return Err(Fail::Conflict(
            "Sweep changed; reload before applying a control".into(),
        ));
    }
    status(State(admin)).await
}
