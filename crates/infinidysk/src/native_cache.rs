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
