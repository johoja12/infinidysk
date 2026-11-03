//! Authenticated inspection only. Mutation/retry authority belongs to the coordinator.
use crate::admin::{Admin, Fail};
use axum::{
    Json,
    extract::{Path, State},
};
use infinidysk_core::ItemId;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use utoipa::ToSchema;
use uuid::Uuid;
#[derive(Serialize, ToSchema)]
#[schema(as=RegrabStatus)]
pub struct Status {
    pub item: Uuid,
    pub arr_id: String,
    pub stage: String,
    pub revision: i64,
    pub command_id: Option<i64>,
    pub created_at: i64,
    pub updated_at: i64,
    pub terminal: bool,
}
#[derive(Deserialize)]
struct Saved {
    arr_id: String,
    progress: infinidysk_arr::Regrab,
}
#[utoipa::path(get,operation_id="regrab_status",path="/api/v1/health/regrabs/{id}",params(("id"=Uuid,Path)),responses((status=200,body=Option<Status>)))]
pub async fn status(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<Option<Status>>, Fail> {
    let Some(row) = admin.vfs.db().regrab_intent(ItemId(id)).await? else {
        return Ok(Json(None));
    };
    let saved: Saved = serde_json::from_str(&row.payload).map_err(|_| {
        Fail::Unavailable("Regrab journal could not be read; retain it for recovery".to_owned())
    })?;
    Ok(Json(Some(Status {
        item: id,
        arr_id: saved.arr_id,
        stage: saved.progress.stage.label().to_owned(),
        revision: row.revision,
        command_id: saved.progress.command,
        created_at: row.created_at,
        updated_at: row.updated_at,
        terminal: row.terminal,
    })))
}
