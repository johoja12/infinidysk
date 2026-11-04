//! Authenticated journal inspection and revision-checked coordinator controls.
use crate::admin::{Admin, Fail};
use axum::{
    Json,
    extract::{Path, Query, State},
};
use infinidysk_core::ItemId;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;
#[derive(Clone, Serialize, ToSchema)]
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
    pub abandoned: bool,
    pub observed_at: Option<i64>,
    pub observation: Option<String>,
}
#[derive(Deserialize)]
struct Saved {
    arr_id: String,
    progress: infinidysk_arr::Regrab,
    #[serde(default)]
    abandoned: bool,
    #[serde(default)]
    observed_at: Option<i64>,
    #[serde(default)]
    observation: Option<String>,
}
#[utoipa::path(get,operation_id="regrab_status",path="/api/v1/health/regrabs/{id}",params(("id"=Uuid,Path)),responses((status=200,body=Option<Status>)))]
pub async fn status(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<Option<Status>>, Fail> {
    Ok(Json(inspect(admin.vfs.db(), id).await?))
}
pub(crate) async fn inspect(db: &infinidysk_db::Db, id: Uuid) -> Result<Option<Status>, Fail> {
    let Some(row) = db.regrab_intent(ItemId(id)).await? else {
        return Ok(None);
    };
    Ok(Some(from_row(id, &row)))
}
pub(crate) fn from_row(id: Uuid, row: &infinidysk_db::RegrabIntent) -> Status {
    match serde_json::from_str::<Saved>(&row.payload) {
        Ok(saved) => Status {
            item: id,
            arr_id: saved.arr_id,
            stage: if saved.progress.version != 1 {
                "unsupported"
            } else if saved.progress.terminal() {
                saved.progress.stage.label()
            } else if saved.abandoned {
                "abandoned"
            } else {
                saved.progress.stage.label()
            }
            .into(),
            revision: row.revision,
            command_id: saved.progress.command,
            created_at: row.created_at,
            updated_at: row.updated_at,
            terminal: row.terminal,
            abandoned: saved.abandoned,
            observed_at: saved.observed_at,
            observation: saved.observation,
        },
        Err(_) => Status {
            item: id,
            arr_id: String::new(),
            stage: "unreadable".into(),
            revision: row.revision,
            command_id: None,
            created_at: row.created_at,
            updated_at: row.updated_at,
            terminal: row.terminal,
            abandoned: false,
            observed_at: None,
            observation: Some("Journal unreadable; original retained for recovery".into()),
        },
    }
}

pub(crate) fn replacement_path(row: &infinidysk_db::RegrabIntent) -> Option<String> {
    let saved: Saved = serde_json::from_str(&row.payload).ok()?;
    (row.terminal && saved.progress.version == 1 && saved.progress.terminal())
        .then(|| saved.progress.owner.file_path().to_owned())
}

#[derive(Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=RegrabOperation)]
pub enum Operation {
    Observe,
    Abandon,
    Resume,
}
#[derive(Deserialize, ToSchema)]
#[schema(as=RegrabControl)]
pub struct Control {
    pub revision: i64,
    pub operation: Operation,
}
#[utoipa::path(post,operation_id="regrab_control",path="/api/v1/health/regrabs/{id}",request_body=Control,params(("id"=Uuid,Path)),responses((status=200,body=Option<Status>)))]
pub async fn control(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(request): Json<Control>,
) -> Result<Json<Option<Status>>, Fail> {
    let operation = match request.operation {
        Operation::Observe => infinidysk_health::RegrabControl::Observe,
        Operation::Abandon => infinidysk_health::RegrabControl::Abandon,
        Operation::Resume => infinidysk_health::RegrabControl::Resume,
    };
    let changed = tokio::time::timeout(
        std::time::Duration::from_secs(15),
        admin
            .health
            .control_regrab(ItemId(id), request.revision, operation),
    )
    .await
    .map_err(|_| {
        Fail::Unavailable(
            "Regrab control timed out; reload its current revision before retrying".into(),
        )
    })?
    .map_err(|e| Fail::Bad(e.to_string()))?;
    if !changed {
        return Err(Fail::Conflict(
            "Journal changed; reload before applying a control".into(),
        ));
    }
    Ok(Json(inspect(admin.vfs.db(), id).await?))
}
#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct PageQuery {
    pub before: Option<i64>,
    pub after: Option<Uuid>,
}
#[derive(Clone, Serialize, ToSchema)]
#[schema(as=RegrabCursor)]
pub struct Cursor {
    pub before: i64,
    pub after: Uuid,
}
#[derive(Serialize, ToSchema)]
#[schema(as=RegrabPage)]
pub struct Page {
    pub entries: Vec<Status>,
    pub next: Option<Cursor>,
}
#[utoipa::path(get,operation_id="regrab_list",path="/api/v1/health/regrabs",params(PageQuery),responses((status=200,body=Page)))]
pub async fn list(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<PageQuery>,
) -> Result<Json<Page>, Fail> {
    let mut rows = admin
        .vfs
        .db()
        .regrab_page(
            query.before.unwrap_or(i64::MAX),
            ItemId(query.after.unwrap_or(Uuid::nil())),
            51,
        )
        .await?;
    let more = rows.len() > 50;
    rows.truncate(50);
    let next = if more {
        rows.last().map(|(id, row)| Cursor {
            before: row.updated_at,
            after: id.0,
        })
    } else {
        None
    };
    Ok(Json(Page {
        entries: rows
            .into_iter()
            .map(|(id, row)| from_row(id.0, &row))
            .collect(),
        next,
    }))
}
