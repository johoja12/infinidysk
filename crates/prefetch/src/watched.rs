//! Finish-watched evidence comes from served foreground bytes, never seek position.
//! Short scanner bursts and repeated polling cannot manufacture watched progress.
use infinidysk_core::ItemId;
use infinidysk_vfs::Playback;
use std::{
    collections::{HashMap, HashSet},
    time::{SystemTime, UNIX_EPOCH},
};
const MIN_BYTES: u64 = 64 * 1024 * 1024;
const MIN_SESSION: i64 = 120_000;
const IDLE: i64 = 30_000;
type Key = (ItemId, String, Option<String>);
#[derive(Default)]
struct Watch {
    started: i64,
    last: i64,
    bytes: u64,
    seen: HashMap<u64, u64>,
}
#[derive(Default)]
pub(super) struct Tracker {
    watches: HashMap<Key, Watch>,
}
fn ms(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}
impl Tracker {
    pub(super) fn observe(&mut self, mut reads: Vec<Playback>, now: i64) -> Vec<(ItemId, u64)> {
        if reads.len() > 2048 {
            self.watches.clear();
            return vec![];
        }
        reads.sort_by_key(|r| (r.opened_at, r.id));
        let live: HashSet<_> = reads.iter().map(|r| r.id).collect();
        self.watches
            .retain(|_, w| now.saturating_sub(w.last) <= 120_000);
        for w in self.watches.values_mut() {
            w.seen.retain(|id, _| live.contains(id));
        }
        for r in reads {
            if r.served == 0 {
                continue;
            }
            let key = (r.item, r.client, r.player);
            if !self.watches.contains_key(&key) && self.watches.len() >= 128 {
                continue;
            }
            let opened = ms(r.opened_at);
            let last = ms(r.closed_at.unwrap_or_else(SystemTime::now)).min(now);
            if opened > now || last < opened {
                continue;
            }
            let w = self.watches.entry(key).or_default();
            if w.started == 0 || opened > w.last.saturating_add(IDLE) {
                *w = Watch {
                    started: opened,
                    ..Watch::default()
                };
            }
            let old = w.seen.insert(r.id, r.served).unwrap_or(0);
            w.bytes = w.bytes.saturating_add(r.served.saturating_sub(old));
            w.last = w.last.max(last);
        }
        let mut qualified = HashMap::<ItemId, u64>::new();
        for ((item, _, _), w) in &self.watches {
            if w.last.saturating_sub(w.started) >= MIN_SESSION && w.bytes >= MIN_BYTES {
                qualified
                    .entry(*item)
                    .and_modify(|bytes| *bytes = (*bytes).max(w.bytes))
                    .or_insert(w.bytes);
            }
        }
        let mut out: Vec<_> = qualified.into_iter().collect();
        out.sort_by_key(|(item, _)| item.0);
        out.truncate(32);
        out
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_vfs::Delivery;
    use std::time::Duration;
    fn read(id: u64, item: ItemId, start: u64, end: u64, served: u64) -> Playback {
        Playback {
            id,
            item,
            delivery: Delivery::WebDav,
            client: "fixture".into(),
            player: None,
            opened_at: UNIX_EPOCH + Duration::from_secs(start),
            closed_at: Some(UNIX_EPOCH + Duration::from_secs(end)),
            served,
            native_cache_bytes: served,
            position: 999_999_999,
        }
    }
    #[test]
    fn scanner_positions_polling_and_idle_gaps_cannot_manufacture_a_watched_session() {
        let item = ItemId::new();
        let mut tracker = Tracker::default();
        let fast = read(1, item, 1000, 1001, MIN_BYTES);
        assert_eq!(
            tracker.observe(vec![fast.clone()], 1_001_000),
            Vec::<(ItemId, u64)>::new()
        );
        assert_eq!(
            tracker.observe(vec![fast], 1_121_000),
            Vec::<(ItemId, u64)>::new()
        );
        let next = read(2, item, 1040, 1160, MIN_BYTES / 2);
        assert_eq!(
            tracker.observe(vec![next], 1_160_000),
            Vec::<(ItemId, u64)>::new()
        );
        let a = read(3, item, 1200, 1260, MIN_BYTES / 2);
        let b = read(4, item, 1270, 1330, MIN_BYTES / 2);
        assert_eq!(
            tracker.observe(vec![a.clone()], 1_260_000),
            Vec::<(ItemId, u64)>::new()
        );
        assert_eq!(
            tracker.observe(vec![a.clone(), b.clone()], 1_330_000),
            [(item, MIN_BYTES)]
        );
        assert_eq!(
            tracker.observe(vec![a, b], 1_331_000),
            [(item, MIN_BYTES)],
            "polling counts each byte once, including native-cache playback"
        );
    }
}
