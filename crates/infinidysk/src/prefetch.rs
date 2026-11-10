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
    let mut queue = admin.prefetch.status().await;
    let recovered = admin
        .vfs
        .db()
        .prefetch_display_labels()
        .await
        .ok()
        .flatten()
        .and_then(|raw| LabelHistory::parse(&raw));
    let settings = admin.config.read().await.settings().clone();
    let snapshot = admin.predictions.snapshot().await;
    if crate::predictions::automatic_scope(&settings).as_deref() == Some(snapshot.scope.as_str()) {
        for job in queue
            .jobs
            .iter_mut()
            .chain(queue.history.iter_mut().map(|h| &mut h.job))
        {
            display_labels(job, &snapshot, &settings);
        }
    }
    if let Some(recovered) = recovered {
        for job in queue
            .jobs
            .iter_mut()
            .chain(queue.history.iter_mut().map(|h| &mut h.job))
        {
            recovered.apply(job);
        }
    }
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

// Enrich only a response copy. Never rewrite historical attribution or use labels
// as source access authority; old policy scopes must match the current settings.
fn display_labels(
    job: &mut Job,
    snapshot: &crate::predictions::Snapshot,
    settings: &crate::config::Settings,
) {
    if job.automatic_scope.as_deref() != Some(snapshot.scope.as_str()) {
        return;
    }
    let Some(origin) = job.attribution.policy.as_mut() else {
        return;
    };
    if origin.viewer_name.is_none() {
        let names: std::collections::HashSet<_> = snapshot
            .predictions
            .iter()
            .filter(|p| p.server == origin.server && p.viewer == origin.viewer)
            .filter_map(|p| p.viewer_name.as_ref())
            .collect();
        if names.len() == 1 {
            origin.viewer_name = names.into_iter().next().cloned();
        }
    }
    if origin.source_title.is_none() || origin.source_path.is_none() {
        let candidates: Vec<_> = settings
            .prefetch
            .plex
            .sources
            .iter()
            .filter(|s| {
                s.enabled
                    && s.server == origin.server
                    && s.viewer == origin.viewer
                    && match s.kind {
                        infinidysk_plex::sources::Kind::Hub => origin.source == "hub",
                        infinidysk_plex::sources::Kind::Collection => origin.source == "collection",
                        infinidysk_plex::sources::Kind::Library => origin.source == "library",
                    }
                    && origin.source_path.as_ref().is_none_or(|p| *p == s.path)
            })
            .collect();
        if let [source] = candidates.as_slice() {
            if origin.source_title.is_none() {
                origin.source_title.clone_from(&source.title);
            }
            if origin.source_path.is_none() {
                origin.source_path = Some(source.path.clone());
            }
        }
    }
}

#[derive(Deserialize)]
struct LabelHistory {
    version: u8,
    records: Vec<RecoveredLabel>,
}
#[derive(Deserialize)]
struct RecoveredLabel {
    scope: String,
    server: String,
    viewer: String,
    source: String,
    viewer_name: Option<String>,
    source_title: Option<String>,
    source_path: Option<String>,
}
impl LabelHistory {
    fn parse(raw: &str) -> Option<Self> {
        if raw.len() > 1_048_576 {
            return None;
        }
        let value: Self = serde_json::from_str(raw).ok()?;
        let mut keys = std::collections::HashSet::new();
        (value.version == 1
            && value.records.len() <= 256
            && value.records.iter().all(|r| {
                r.scope.len() == 64
                    && r.scope.bytes().all(|b| b.is_ascii_hexdigit())
                    && !r.server.is_empty()
                    && r.server.len() <= 256
                    && !r.viewer.is_empty()
                    && r.viewer.len() <= 256
                    && matches!(
                        r.source.as_str(),
                        "playback"
                            | "next_episode"
                            | "partial_movie"
                            | "hub"
                            | "collection"
                            | "library"
                    )
                    && r.viewer_name
                        .as_ref()
                        .is_none_or(|s| !s.trim().is_empty() && s.len() <= 1024)
                    && r.source_title.as_ref().is_none_or(|s| s.len() <= 4096)
                    && r.source_path.as_ref().is_none_or(|s| {
                        infinidysk_plex::sources::Source {
                            server: r.server.clone(),
                            viewer: r.viewer.clone(),
                            path: s.clone(),
                            kind: match r.source.as_str() {
                                "collection" => infinidysk_plex::sources::Kind::Collection,
                                "library" => infinidysk_plex::sources::Kind::Library,
                                _ => infinidysk_plex::sources::Kind::Hub,
                            },
                            ..Default::default()
                        }
                        .validate()
                        .is_ok()
                    })
                    && keys.insert((&r.scope, &r.server, &r.viewer, &r.source))
            }))
        .then_some(value)
    }
    fn apply(&self, job: &mut Job) {
        let Some(origin) = job.attribution.policy.as_mut() else {
            return;
        };
        let Some(label) = self.records.iter().find(|r| {
            job.automatic_scope.as_deref() == Some(&r.scope)
                && origin.server == r.server
                && origin.viewer == r.viewer
                && origin.source == r.source
        }) else {
            return;
        };
        if origin.viewer_name.is_none() {
            origin.viewer_name.clone_from(&label.viewer_name);
        }
        if origin.source_title.is_none() {
            origin.source_title.clone_from(&label.source_title);
        }
        if origin.source_path.is_none() {
            origin.source_path.clone_from(&label.source_path);
        }
    }
}

#[cfg(test)]
mod recovered_label_tests {
    use super::*;
    #[test]
    fn retained_labels_require_unique_exact_contexts_and_safe_resources() {
        let record = serde_json::json!({"scope":"a".repeat(64),"server":"fixture","viewer":"1","source":"hub","viewer_name":"Fixture viewer","source_title":"Fixture hub","source_path":"/hubs/fixture"});
        let raw = serde_json::json!({"version":1,"records":[record]}).to_string();
        assert!(LabelHistory::parse(&raw).is_some());
        let raw = serde_json::json!({"version":1,"records":[record,record]}).to_string();
        assert!(LabelHistory::parse(&raw).is_none());
        let mut bad = record;
        bad["source_path"] = serde_json::json!("https://example.com/?token=secret");
        assert!(
            LabelHistory::parse(&serde_json::json!({"version":1,"records":[bad]}).to_string())
                .is_none()
        );
    }
}
