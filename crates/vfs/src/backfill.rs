//! Bounded nonblocking handoff of actual uncached foreground ranges. The warming
//! queue acknowledges a hint only after a durable source-bound admission.
use infinidysk_core::ItemId;
use infinidysk_native_cache::{BLOCK, Key};
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex, PoisonError},
    time::{Duration, Instant},
};
const MAX_PENDING: usize = 256;
const QUALIFY: u64 = 4 * BLOCK;

#[derive(Clone, Debug)]
pub struct Backfill {
    pub id: u64,
    pub item: ItemId,
    pub revision: String,
    pub size: u64,
    pub start: u64,
    pub end: u64,
    due: Instant,
}
#[derive(Default)]
struct State {
    next: u64,
    pending: VecDeque<Backfill>,
    dropped: u64,
}
#[derive(Default)]
pub struct Backfills {
    state: Mutex<State>,
}
impl Backfills {
    fn record(&self, key: &Key, start: u64, end: u64) {
        if end <= start || start >= key.size() {
            return;
        }
        let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        let mut start = start / BLOCK * BLOCK;
        let mut end = end
            .checked_next_multiple_of(BLOCK)
            .unwrap_or_else(|| key.size())
            .min(key.size());
        let mut due = Instant::now() + Duration::from_secs(2);
        let revision = key.revision();
        while let Some(index) = s.pending.iter().position(|entry| {
            entry.item.0 == key.item()
                && entry.revision == revision
                && entry.start <= end
                && entry.end >= start
        }) {
            let entry = s
                .pending
                .remove(index)
                .expect("position belongs to the pending queue");
            start = start.min(entry.start);
            end = end.max(entry.end);
            due = due.min(entry.due);
        }
        if s.pending.len() >= MAX_PENDING {
            s.dropped = s.dropped.saturating_add(1);
            return;
        }
        s.next = s.next.wrapping_add(1);
        let id = s.next;
        s.pending.push_back(Backfill {
            id,
            item: ItemId(key.item()),
            revision: revision.into(),
            size: key.size(),
            start,
            end,
            due,
        });
    }
    #[must_use]
    pub fn pending(&self) -> Vec<Backfill> {
        let s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        s.pending
            .iter()
            .filter(|r| r.due <= Instant::now())
            .take(32)
            .cloned()
            .collect()
    }
    pub fn acknowledge(&self, id: u64) {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .pending
            .retain(|r| r.id != id);
    }
    #[must_use]
    pub fn stats(&self) -> (usize, u64) {
        let s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        (s.pending.len(), s.dropped)
    }
}
/// Only actual served bytes qualify a handle. Seek offsets and repeated polling
/// cannot turn short scanner probes into requests. At most 64 disjoint ranges.
pub(crate) struct Tracker {
    journal: Arc<Backfills>,
    key: Key,
    served: u64,
    ranges: Vec<(u64, u64)>,
    pending_bytes: u64,
}
impl Tracker {
    pub(crate) fn new(journal: Arc<Backfills>, key: Key) -> Self {
        Self {
            journal,
            key,
            served: 0,
            ranges: Vec::new(),
            pending_bytes: 0,
        }
    }
    pub(crate) fn served(&mut self, bytes: u64) {
        self.served = self.served.saturating_add(bytes);
    }
    pub(crate) fn miss(&mut self, start: u64, end: u64) {
        if end <= start {
            return;
        }
        if let Some(last) = self.ranges.last_mut().filter(|last| last.1 == start) {
            last.1 = end;
        } else if self.ranges.len() < 64 {
            self.ranges.push((start, end));
        }
        self.pending_bytes = self.pending_bytes.saturating_add(end - start);
        if self.pending_bytes >= QUALIFY {
            self.flush();
        }
    }
    fn flush(&mut self) {
        if self.served < QUALIFY {
            return;
        }
        for (start, end) in self.ranges.drain(..) {
            self.journal.record(&self.key, start, end);
        }
        self.pending_bytes = 0;
    }
}
impl Drop for Tracker {
    fn drop(&mut self) {
        self.flush();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn key(item: ItemId, revision: u8) -> Key {
        Key::from_fingerprint(item.0, [revision; 32], 100 * BLOCK)
    }
    #[test]
    fn scanner_seeks_never_qualify_but_real_served_ranges_are_aligned_and_merged() {
        let journal = Arc::new(Backfills::default());
        let item = ItemId::new();
        let key = key(item, 1);
        {
            let mut tracker = Tracker::new(journal.clone(), key.clone());
            tracker.served(1024);
            tracker.miss(70 * BLOCK + 1, 70 * BLOCK + 1025);
        }
        assert_eq!(journal.stats(), (0, 0));
        {
            let mut tracker = Tracker::new(journal.clone(), key);
            tracker.served(4 * BLOCK);
            tracker.miss(BLOCK + 1, 3 * BLOCK + 1);
            tracker.miss(3 * BLOCK + 1, 5 * BLOCK);
        }
        let state = journal.state.lock().unwrap();
        assert_eq!(state.pending.len(), 1);
        let entry = state.pending[0].clone();
        drop(state);
        assert_eq!((entry.start, entry.end), (BLOCK, 5 * BLOCK));
    }
    #[test]
    fn stale_ack_cannot_lose_a_coalesced_range_and_other_revisions_stay_separate() {
        let journal = Backfills::default();
        let item = ItemId::new();
        let first = key(item, 1);
        journal.record(&first, 0, BLOCK);
        let old = journal.state.lock().unwrap().pending[0].id;
        journal.record(&first, BLOCK, 2 * BLOCK);
        journal.acknowledge(old);
        assert_eq!(journal.stats().0, 1);
        journal.record(&key(item, 2), 0, BLOCK);
        assert_eq!(journal.stats().0, 2);
        let state = journal.state.lock().unwrap();
        assert_eq!(state.pending[0].end, 2 * BLOCK);
    }
    #[test]
    fn queue_is_bounded_and_overflow_does_not_evict_existing_hints() {
        let journal = Backfills::default();
        for _ in 0..MAX_PENDING + 2 {
            journal.record(&key(ItemId::new(), 1), 0, BLOCK);
        }
        assert_eq!(journal.stats(), (MAX_PENDING, 2));
    }
    #[test]
    fn a_bridge_coalesces_previously_unordered_disjoint_ranges() {
        let journal = Backfills::default();
        let key = key(ItemId::new(), 1);
        journal.record(&key, 4 * BLOCK, 6 * BLOCK);
        journal.record(&key, 0, 2 * BLOCK);
        journal.record(&key, 2 * BLOCK, 4 * BLOCK);
        let state = journal.state.lock().unwrap();
        assert_eq!(state.pending.len(), 1);
        assert_eq!(
            (state.pending[0].start, state.pending[0].end),
            (0, 6 * BLOCK)
        );
    }
}
