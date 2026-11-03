//! Source/client attribution across short handle bursts. Client labels do not prove a
//! Plex viewer; missing viewer evidence stays explicit rather than guessing by title.
use crate::admin::Admin;
use axum::{Json, extract::State};
use infinidysk_vfs::Playback;
use serde::Serialize;
use std::{
    collections::BTreeMap,
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
            });
        }
    }
    let mut out: Vec<_> = grouped.into_values().flatten().collect();
    out.sort_by_key(|r| std::cmp::Reverse((r.last_seen_at, r.id)));
    out
}
#[utoipa::path(get,operation_id="library_playback",path="/api/v1/library/playback",responses((status=200,body=Vec<Burst>)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Json<Vec<Burst>> {
    Json(grouped(admin.vfs.sessions().playback()))
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
}
