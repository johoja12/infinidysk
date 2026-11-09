//! Current repair follow-up is separate from immutable warming outcomes. Queue
//! status reads only this bounded snapshot; NAS inspection belongs to owned work.
use crate::{admin::Admin, config::Settings, regrabs};
use infinidysk_core::ItemId;
use infinidysk_db::now_ms;
use infinidysk_prefetch::{Job, State};
use serde::Serialize;
use sha2::{Digest as _, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet},
    sync::{Arc, Mutex, PoisonError},
    time::{Duration, Instant},
};
use utoipa::ToSchema;
use uuid::Uuid;
#[derive(Clone, Serialize, ToSchema)]
#[schema(as=PrefetchRepairFollowup)]
pub struct Followup {
    pub item: Uuid,
    pub regrab: regrabs::Status,
    pub status: String,
    pub replacement_item: Option<Uuid>,
    pub replacement_revision: Option<String>,
    pub warmed_at: Option<i64>,
}
#[derive(Clone, Default, Serialize, ToSchema)]
#[schema(as=PrefetchRepairSnapshot)]
pub struct Snapshot {
    pub updated_at: Option<i64>,
    pub refreshing: bool,
    pub stale: bool,
    pub available: bool,
    pub entries: Vec<Followup>,
    pub error: Option<String>,
}
#[derive(Default)]
struct Inner {
    scope: Option<[u8; 32]>,
    attempt_scope: Option<[u8; 32]>,
    attempt: Option<Instant>,
    updated: Option<Instant>,
    busy: bool,
    snapshot: Snapshot,
}
#[derive(Clone, Default)]
pub struct Followups {
    inner: Arc<Mutex<Inner>>,
}
struct Refresh(Arc<Mutex<Inner>>);
impl Drop for Refresh {
    fn drop(&mut self) {
        self.0.lock().unwrap_or_else(PoisonError::into_inner).busy = false;
    }
}
fn scope(admin: &Admin, settings: &Settings) -> Option<[u8; 32]> {
    serde_json::to_vec(&(
        &settings.imports,
        &settings.mount,
        &settings.arr_instances,
        admin.vfs.db().blob_epoch(),
        admin.client.repair_generation(),
    ))
    .ok()
    .map(|b| Sha256::digest(b).into())
}
impl Followups {
    pub async fn snapshot(&self, admin: Arc<Admin>, jobs: Vec<Job>) -> Snapshot {
        let settings = admin.config.read().await.settings().clone();
        let Some(expected) = scope(&admin, &settings) else {
            return Snapshot {
                stale: true,
                error: Some("Repair follow-up configuration is unavailable".into()),
                ..Snapshot::default()
            };
        };
        let mut state = self.inner.lock().unwrap_or_else(PoisonError::into_inner);
        if jobs.is_empty() {
            state.scope = Some(expected);
            state.updated = Some(Instant::now());
            state.snapshot = Snapshot {
                updated_at: Some(now_ms()),
                available: true,
                ..Snapshot::default()
            };
            return state.snapshot.clone();
        }

        let due = !state.busy
            && (state.attempt_scope != Some(expected)
                || state
                    .attempt
                    .is_none_or(|t| t.elapsed() >= Duration::from_secs(15)));
        if due {
            state.busy = true;
            state.attempt = Some(Instant::now());
            state.attempt_scope = Some(expected);
        }
        let mut result = if state.scope == Some(expected) {
            state.snapshot.clone()
        } else {
            Snapshot {
                available: true,
                ..Snapshot::default()
            }
        };
        result.refreshing = state.busy;
        result.stale = state.scope != Some(expected)
            || state
                .updated
                .is_none_or(|t| t.elapsed() > Duration::from_secs(30))
            || result.error.is_some();
        drop(state);
        if due {
            let owner = self.clone();
            tokio::spawn(async move {
                let _refresh = Refresh(Arc::clone(&owner.inner));
                let outcome = tokio::select! {()=admin.cancel.cancelled()=>return,result=tokio::time::timeout(Duration::from_secs(25),collect(&admin,&settings,&jobs))=>result};
                let settings = admin.config.read().await.settings().clone();
                if scope(&admin, &settings) != Some(expected) {
                    return;
                }
                let mut state = owner.inner.lock().unwrap_or_else(PoisonError::into_inner);
                if let Ok(Ok(entries)) = outcome {
                    state.scope = Some(expected);
                    state.updated = Some(Instant::now());
                    state.snapshot = Snapshot {
                        updated_at: Some(now_ms()),
                        refreshing: false,
                        stale: false,
                        available: true,
                        entries,
                        error: None,
                    };
                } else {
                    if state.scope != Some(expected) {
                        state.scope = Some(expected);
                        state.updated = None;
                        state.snapshot = Snapshot::default();
                    }
                    state.snapshot.available = false;
                    state.snapshot.error=Some("Repair follow-up inspection is unavailable; retained results need a fresh check".into());
                }
            });
        }
        result
    }
}
async fn collect(admin: &Admin, settings: &Settings, jobs: &[Job]) -> Result<Vec<Followup>, ()> {
    let mut recent = BTreeMap::<Uuid, i64>::new();
    for job in jobs {
        recent
            .entry(job.item)
            .and_modify(|at| *at = (*at).max(job.updated_at))
            .or_insert(job.updated_at);
    }
    let mut ids: Vec<_> = recent.into_iter().collect();
    ids.sort_by_key(|(id, at)| (std::cmp::Reverse(*at), *id));
    let mut entries = Vec::new();
    let mut inspected = 0;
    for (id, _) in ids.into_iter().take(32) {
        let Some(row) = admin
            .vfs
            .db()
            .regrab_intent(ItemId(id))
            .await
            .map_err(|_| ())?
        else {
            continue;
        };
        let regrab = regrabs::from_row(id, &row);
        let mut entry = Followup {
            item: id,
            status: regrab.stage.clone(),
            regrab,
            replacement_item: None,
            replacement_revision: None,
            warmed_at: None,
        };
        if entry.status == "replaced" {
            entry.status = "replacement_unavailable".into();
            if inspected < 8
                && let Some(arr_path) = regrabs::replacement_path(&row)
            {
                let paths: BTreeSet<_> = settings
                    .imports
                    .library_dirs
                    .iter()
                    .filter_map(|root| {
                        crate::library::local(std::slice::from_ref(root), &arr_path)
                            .map(|path| (path, root.path.clone()))
                    })
                    .collect();
                if paths.len() == 1
                    && let Some((path, root)) = paths.into_iter().next()
                {
                    inspected += 1;
                    if let Ok(Some(replacement)) = admin
                        .library
                        .replacement_pointer(path.to_string_lossy().into_owned(), root)
                        .await
                        && replacement.0 != id
                        && let Some(item) =
                            admin.vfs.db().item(replacement).await.map_err(|_| ())?
                        && item.health_state != infinidysk_db::HealthState::Hidden
                        && let Ok(key) = admin.vfs.cache_key(&item).await
                    {
                        entry.replacement_item = Some(replacement.0);
                        entry.replacement_revision = Some(key.revision().into());
                        entry.warmed_at = whole_warmed(
                            jobs,
                            replacement.0,
                            key.revision(),
                            key.size(),
                            row.created_at,
                        );
                        entry.status = if entry.warmed_at.is_some() {
                            "replacement_warmed"
                        } else {
                            "replacement"
                        }
                        .into();
                    }
                }
            }
        }
        entries.push(entry);
    }
    Ok(entries)
}
fn whole_warmed(
    jobs: &[Job],
    item: Uuid,
    revision: &str,
    size: u64,
    requested: i64,
) -> Option<i64> {
    jobs.iter()
        .filter(|j| {
            j.item == item
                && j.revision == revision
                && j.size == size
                && size > 0
                && j.state == State::Complete
                && j.ranges.len() == 1
                && j.ranges[0].start == 0
                && j.ranges[0].end == size
                && j.verified_bytes >= size
                && j.updated_at >= requested
        })
        .map(|j| j.updated_at)
        .max()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn replacement_requires_a_whole_current_revision_warmed_after_the_request() {
        let id = Uuid::new_v4();
        let mut job:Job=serde_json::from_value(serde_json::json!({"id":Uuid::new_v4(),"item":id,"revision":"a".repeat(64),"size":100,"ranges":[{"start":0,"end":100}],"priority":50,"intent":Uuid::new_v4(),"state":"complete","created_at":1,"updated_at":200,"expires_at":1000,"next_attempt":0,"attempts":0,"verified_bytes":100,"source_bytes":100,"cached_bytes":0,"reason":null,"range":1,"offset":100})).unwrap();
        assert_eq!(
            whole_warmed(&[job.clone()], id, &job.revision, 100, 100),
            Some(200)
        );
        assert_eq!(
            whole_warmed(&[job.clone()], id, &"b".repeat(64), 100, 100),
            None
        );
        assert_eq!(
            whole_warmed(&[job.clone()], id, &job.revision, 100, 201),
            None
        );
        job.ranges[0].end = 50;
        assert_eq!(
            whole_warmed(&[job.clone()], id, &job.revision, 100, 100),
            None
        );
        job.ranges[0].end = 100;
        job.verified_bytes = 99;
        assert_eq!(
            whole_warmed(&[job.clone()], id, &job.revision, 100, 100),
            None
        );
        job.verified_bytes = 100;
        job.state = State::Queued;
        assert_eq!(
            whole_warmed(&[job.clone()], id, &job.revision, 100, 100),
            None
        );
    }
}
