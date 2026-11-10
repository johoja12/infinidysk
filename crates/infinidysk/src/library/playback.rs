//! Source/client attribution across short handle bursts. Client labels do not prove a
//! Plex viewer; missing viewer evidence stays explicit rather than guessing by title.
use crate::admin::Admin;
use axum::{Json, extract::State};
use infinidysk_vfs::Playback;
use serde::Serialize;
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    sync::Arc,
    time::{SystemTime, UNIX_EPOCH},
};
use utoipa::ToSchema;
use uuid::Uuid;

#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=LibraryPlayback)]
pub struct Burst {
    pub id: u64,
    pub item: Uuid,
    pub delivery: String,
    pub client: String,
    pub player: Option<String>,
    pub handles: u32,
    pub active_handles: u32,
    pub opened_at: i64,
    pub last_seen_at: i64,
    pub served_bytes: u64,
    pub native_cache_bytes: u64,
    pub position_bytes: u64,
    /// Selected Plex sessions reported for this exact source revision. These do
    /// not identify which viewer caused a particular HTTP/FUSE read.
    pub plex_sessions: Vec<ReportedSession>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[schema(as=LibraryReportedPlexSession)]
pub struct ReportedSession {
    pub server: String,
    pub viewer: String,
    pub context: crate::predictions::PlaybackContext,
    pub observed_at: i64,
}
fn ms(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}
pub(crate) fn grouped(mut reads: Vec<Playback>) -> Vec<Burst> {
    type Key = (Uuid, String, String, Option<String>);
    reads.sort_by_key(|r| (r.opened_at, r.id));
    let mut grouped: BTreeMap<Key, Vec<Burst>> = BTreeMap::new();
    for r in reads {
        if r.served == 0 && r.closed_at.is_some() {
            continue;
        }
        let key = (
            r.item.0,
            r.delivery.label().to_owned(),
            r.client.clone(),
            r.player.clone(),
        );
        let list = grouped.entry(key).or_default();
        let opened = ms(r.opened_at);
        let seen = ms(r.closed_at.unwrap_or_else(SystemTime::now));
        if let Some(last) = list.last_mut().filter(|last| {
            last.active_handles > 0 || opened.saturating_sub(last.last_seen_at) <= 30_000
        }) {
            last.handles += 1;
            last.active_handles += u32::from(r.closed_at.is_none());
            last.last_seen_at = last.last_seen_at.max(seen);
            last.served_bytes = last.served_bytes.saturating_add(r.served);
            last.native_cache_bytes = last.native_cache_bytes.saturating_add(r.native_cache_bytes);
            last.position_bytes = r.position;
        } else {
            list.push(Burst {
                id: r.id,
                item: r.item.0,
                delivery: r.delivery.label().into(),
                client: r.client,
                player: r.player,
                handles: 1,
                active_handles: u32::from(r.closed_at.is_none()),
                opened_at: opened,
                last_seen_at: seen,
                served_bytes: r.served,
                native_cache_bytes: r.native_cache_bytes,
                position_bytes: r.position,
                plex_sessions: Vec::new(),
            });
        }
    }
    let mut out: Vec<_> = grouped.into_values().flatten().collect();
    out.sort_by_key(|r| std::cmp::Reverse((r.last_seen_at, r.id)));
    out
}
#[utoipa::path(get,operation_id="library_playback",path="/api/v1/library/playback",responses((status=200,body=Vec<Burst>)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Json<Vec<Burst>> {
    Json(correlated(&admin, grouped(admin.vfs.sessions().playback())).await)
}

/// No Plex network access on the read/status path. Scope changes, restored/stale
/// evidence and unavailable current source metadata all leave attribution unknown.
pub(crate) async fn correlated(admin: &Admin, mut rows: Vec<Burst>) -> Vec<Burst> {
    let snapshot = admin.predictions.snapshot().await;
    let wanted: HashSet<_> = rows.iter().map(|row| row.item).collect();
    let mut revisions = HashMap::new();
    if fresh(&snapshot, infinidysk_db::now_ms()) {
        for id in snapshot
            .predictions
            .iter()
            .filter(|p| !p.playback.is_empty())
            .filter_map(|p| p.item)
            .filter(|id| wanted.contains(id))
            .collect::<std::collections::BTreeSet<_>>()
            .into_iter()
            .take(32)
        {
            if let Ok(Some(item)) = admin.vfs.db().item(infinidysk_core::ItemId(id)).await
                && let Ok(key) = admin.vfs.cache_key(&item).await
            {
                revisions.insert(id, key.revision().to_owned());
            }
        }
    }
    let latest = admin.predictions.snapshot().await;
    if latest.scope == snapshot.scope && latest.updated_at == snapshot.updated_at && !latest.stale {
        attach(&snapshot, &revisions, &mut rows, infinidysk_db::now_ms());
    }
    rows
}
fn fresh(snapshot: &crate::predictions::Snapshot, now: i64) -> bool {
    !snapshot.stale
        && snapshot.error.is_none()
        && snapshot
            .updated_at
            .is_some_and(|at| now >= at && now - at <= 120_000)
}
fn attach(
    snapshot: &crate::predictions::Snapshot,
    revisions: &HashMap<Uuid, String>,
    rows: &mut [Burst],
    now: i64,
) {
    for row in rows.iter_mut() {
        row.plex_sessions.clear();
    }
    if !fresh(snapshot, now) {
        return;
    }
    let at = snapshot.updated_at.unwrap_or(0);
    for p in snapshot
        .predictions
        .iter()
        .filter(|p| p.source == crate::predictions::Source::Playback)
    {
        let Some(item) = p
            .item
            .filter(|id| revisions.get(id) == p.revision.as_ref() && p.revision.is_some())
        else {
            continue;
        };
        for row in rows.iter_mut().filter(|row| {
            row.item == item
                && row.last_seen_at >= at.saturating_sub(30_000)
                && row.opened_at <= at.saturating_add(120_000)
        }) {
            for context in &p.playback {
                let session = ReportedSession {
                    server: p.server.clone(),
                    viewer: p.viewer.clone(),
                    context: context.clone(),
                    observed_at: at,
                };
                if row.plex_sessions.len() < 32 && !row.plex_sessions.contains(&session) {
                    row.plex_sessions.push(session);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_core::ItemId;
    use infinidysk_vfs::Delivery;
    use std::time::Duration;
    #[test]
    fn groups_only_same_source_delivery_client_player_within_idle_window() {
        let base = SystemTime::now();
        let item = ItemId::new();
        let make = |id, at| Playback {
            id,
            item,
            delivery: Delivery::WebDav,
            client: "fixture".into(),
            player: Some("player".into()),
            opened_at: base + Duration::from_secs(at),
            closed_at: Some(base + Duration::from_secs(at + 1)),
            served: 10,
            native_cache_bytes: 7,
            position: 10,
        };
        let a = make(1, 0);
        let b = make(2, 5);
        let c = make(3, 40);
        let different = Playback {
            client: "other-client".into(),
            ..make(4, 6)
        };
        let rows = grouped(vec![c, b, different, a]);
        assert_eq!(rows.len(), 3);
        let joined = rows.iter().find(|r| r.id == 1).unwrap();
        assert_eq!(
            (
                joined.handles,
                joined.served_bytes,
                joined.native_cache_bytes
            ),
            (2, 20, 14)
        );
    }
    #[test]
    fn source_context_requires_fresh_matching_revision_and_read_window_and_preserves_ambiguity() {
        use crate::predictions::{PlaybackContext, Prediction, Snapshot, Source};
        let item = ItemId::new();
        let revision = "a".repeat(64);
        let prediction = Prediction {
            id: Uuid::nil(),
            server: "server".into(),
            viewer: "viewer-a".into(),
            viewer_name: None,
            source_title: None,
            source_path: None,
            rating_key: "fixture".into(),
            title: "fixture".into(),
            source: Source::Playback,
            item: Some(item.0),
            revision: Some(revision.clone()),
            size: 100,
            ranges: vec![],
            warning: None,
            playback: vec![PlaybackContext {
                session: "session-a".into(),
                viewer_name: Some("Fixture A".into()),
                player: Some("Fixture player".into()),
                state: "playing".into(),
                duration_ms: 60_000,
            }],
        };
        let mut other = prediction.clone();
        other.viewer = "viewer-b".into();
        other.playback[0].session = "session-b".into();
        let mut snapshot = Snapshot {
            scope: "scope".into(),
            predictions: vec![prediction, other],
            updated_at: Some(200_000),
            refreshing: false,
            stale: false,
            error: None,
            warning: None,
        };
        let mut rows = grouped(vec![Playback {
            id: 1,
            item,
            delivery: Delivery::Fuse,
            client: "fixture-process".into(),
            player: None,
            opened_at: UNIX_EPOCH + Duration::from_secs(190),
            closed_at: Some(UNIX_EPOCH + Duration::from_secs(205)),
            served: 100,
            native_cache_bytes: 100,
            position: 100,
        }]);
        let revisions = HashMap::from([(item.0, revision)]);
        attach(&snapshot, &revisions, &mut rows, 210_000);
        assert_eq!(
            rows[0].plex_sessions.len(),
            2,
            "both viewers remain candidates for the source, with no chosen reader identity"
        );
        attach(
            &snapshot,
            &HashMap::from([(item.0, "b".repeat(64))]),
            &mut rows,
            210_000,
        );
        assert_eq!(
            rows[0].plex_sessions.len(),
            0,
            "same item ID with a changed source is not the same activity"
        );
        snapshot.stale = true;
        attach(&snapshot, &revisions, &mut rows, 210_000);
        assert_eq!(rows[0].plex_sessions.len(), 0);
        snapshot.stale = false;
        attach(&snapshot, &revisions, &mut rows, 320_001);
        assert_eq!(
            rows[0].plex_sessions.len(),
            0,
            "old source sessions expire even with a long refresh interval"
        );
        attach(&snapshot, &revisions, &mut rows, 199_999);
        assert_eq!(rows[0].plex_sessions.len(), 0, "future evidence is unknown");
        rows[0].last_seen_at = 169_999;
        attach(&snapshot, &revisions, &mut rows, 210_000);
        assert_eq!(
            rows[0].plex_sessions.len(),
            0,
            "a later session cannot label an older unrelated read burst"
        );
    }
}
