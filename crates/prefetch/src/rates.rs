//! Bounded process-local rates. Monotonic time controls rates/stalls; wall time is
//! only a display stamp. Source fills and metered response bytes stay separate.
use serde::Serialize;
use std::{
    collections::{HashMap, VecDeque},
    sync::{Mutex, PoisonError},
    time::{Duration, Instant},
};
use utoipa::ToSchema;
use uuid::Uuid;
const WINDOW: Duration = Duration::from_secs(20);
const STALL: Duration = Duration::from_secs(60);
const MAX_TRACKS: usize = 64;
const MAX_SAMPLES: usize = 512;
type Key = (Uuid, u32);
struct Track {
    started: Instant,
    last: Instant,
    wall: i64,
    samples: VecDeque<(Instant, u64)>,
}
impl Track {
    fn new(at: Instant, wall: i64) -> Self {
        Self {
            started: at,
            last: at,
            wall,
            samples: VecDeque::new(),
        }
    }
    fn record(&mut self, bytes: u64, at: Instant, wall: i64) {
        self.last = at;
        self.wall = wall;
        if bytes > 0 {
            self.samples.push_back((at, bytes));
        }
        self.trim(at);
    }
    fn trim(&mut self, at: Instant) {
        while self
            .samples
            .front()
            .is_some_and(|(stamp, _)| age(at, *stamp) > WINDOW)
            || self.samples.len() > MAX_SAMPLES
        {
            self.samples.pop_front();
        }
    }
    #[allow(clippy::cast_precision_loss)] // Rounded display rates; counters retain exact integer bytes.
    fn rate(&mut self, at: Instant) -> Option<f64> {
        self.trim(at);
        let span = age(at, self.started).min(WINDOW).as_secs_f64();
        (span >= 1.0).then(|| {
            self.samples
                .iter()
                .map(|(_, n)| *n)
                .fold(0u64, u64::saturating_add) as f64
                / span
        })
    }
}
fn age(now: Instant, old: Instant) -> Duration {
    now.checked_duration_since(old).unwrap_or_default()
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=PrefetchRate)]
pub struct Rate {
    pub job: Uuid,
    pub run: u32,
    pub active: bool,
    pub recent_fill_bytes_per_second: Option<f64>,
    pub last_progress_at: i64,
    pub stalled: bool,
}
#[derive(Default)]
struct Inner {
    tracks: HashMap<Key, Track>,
    active: Option<Key>,
    wire: Option<(u64, Track)>,
}
#[derive(Default)]
pub(super) struct Window {
    inner: Mutex<Inner>,
}
pub(super) struct Active<'a> {
    owner: &'a Window,
    key: Key,
}
impl Drop for Active<'_> {
    fn drop(&mut self) {
        let mut state = self
            .owner
            .inner
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if state.active == Some(self.key) {
            state.active = None;
        }
    }
}
impl Window {
    pub(super) fn begin(&self, job: Uuid, run: u32, at: Instant, wall: i64) -> Active<'_> {
        let mut state = self.inner.lock().unwrap_or_else(PoisonError::into_inner);
        let key = (job, run);
        if !state.tracks.contains_key(&key)
            && state.tracks.len() >= MAX_TRACKS
            && let Some(old) = state
                .tracks
                .iter()
                .min_by_key(|(_, t)| t.last)
                .map(|(k, _)| *k)
        {
            state.tracks.remove(&old);
        }
        state
            .tracks
            .entry(key)
            .or_insert_with(|| Track::new(at, wall));
        state.active = Some(key);
        drop(state);
        Active { owner: self, key }
    }
    pub(super) fn record(&self, job: Uuid, run: u32, bytes: u64, at: Instant, wall: i64) {
        if let Some(track) = self
            .inner
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .tracks
            .get_mut(&(job, run))
        {
            track.record(bytes, at, wall);
        }
    }
    pub(super) fn forget(&self, job: Option<Uuid>) {
        let mut state = self.inner.lock().unwrap_or_else(PoisonError::into_inner);
        state
            .tracks
            .retain(|(id, _), _| job.is_some_and(|j| j != *id));
        if job.is_none() || state.active.is_some_and(|(id, _)| Some(id) == job) {
            state.active = None;
        }
    }
    pub(super) fn observe_wire(&self, at: Instant, wall: i64, bytes: u64) {
        let mut state = self.inner.lock().unwrap_or_else(PoisonError::into_inner);
        update_wire(&mut state, at, wall, bytes);
    }
    pub(super) fn snapshot(
        &self,
        at: Instant,
        wall: i64,
        wire_bytes: u64,
    ) -> (Vec<Rate>, Option<f64>) {
        let mut state = self.inner.lock().unwrap_or_else(PoisonError::into_inner);
        let active = state.active;
        let mut rates: Vec<_> = state
            .tracks
            .iter_mut()
            .map(|(&(job, run), t)| Rate {
                job,
                run,
                active: active == Some((job, run)),
                recent_fill_bytes_per_second: t.rate(at),
                last_progress_at: t.wall,
                stalled: active == Some((job, run)) && age(at, t.last) >= STALL,
            })
            .collect();
        rates.sort_by_key(|r| (r.job, r.run));
        update_wire(&mut state, at, wall, wire_bytes);
        let (_, wire) = state
            .wire
            .as_mut()
            .expect("wire observation initialized the track");
        let rate = wire.rate(at);
        drop(state);
        (rates, rate)
    }
}
fn update_wire(state: &mut Inner, at: Instant, wall: i64, bytes: u64) {
    let (previous, track) = state
        .wire
        .get_or_insert_with(|| (bytes, Track::new(at, wall)));
    // UI polling must not overflow the bounded window or alter its sampling rate.
    if age(at, track.last) >= Duration::from_millis(250) {
        track.record(bytes.saturating_sub(*previous), at, wall);
        *previous = bytes;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cached_progress_and_wire_drains_do_not_manufacture_source_fill_rate() {
        let window = Window::default();
        let now = Instant::now();
        let id = Uuid::new_v4();
        let guard = window.begin(id, 0, now, 1000);
        assert!(
            window.snapshot(now, 1000, 0).0[0]
                .recent_fill_bytes_per_second
                .is_none()
        );
        window.record(id, 0, 4_000_000, now + Duration::from_secs(1), 2000);
        let (rates, wire) = window.snapshot(now + Duration::from_secs(2), 3000, 1000);
        assert_eq!(rates[0].recent_fill_bytes_per_second, Some(2_000_000.0));
        assert_eq!(wire, Some(500.0));
        window.record(id, 0, 0, now + Duration::from_secs(10), 500);
        let (rates, _) = window.snapshot(now + Duration::from_secs(21), 100, 1000);
        assert_eq!(rates[0].recent_fill_bytes_per_second, Some(200_000.0));
        assert_eq!(rates[0].last_progress_at, 500);
        assert!(!rates[0].stalled);
        let (rates, _) = window.snapshot(now + Duration::from_secs(71), 0, 1000);
        assert_eq!(rates[0].recent_fill_bytes_per_second, Some(0.0));
        assert!(
            rates[0].stalled,
            "wall-clock rollback cannot hide a stalled physical step"
        );
        drop(guard);
        let (rates, wire) = window.snapshot(now + Duration::from_secs(72), 0, 2000);
        assert!(!rates[0].active);
        assert!(!rates[0].stalled);
        assert_eq!(
            wire,
            Some(50.0),
            "abandoned response bytes remain in the global wire rate"
        );
    }
    #[test]
    fn retries_get_new_windows_and_track_memory_is_bounded() {
        let window = Window::default();
        let now = Instant::now();
        let id = Uuid::new_v4();
        drop(window.begin(id, 0, now, 1));
        window.record(id, 0, 100, now, 1);
        let guard = window.begin(id, 1, now + Duration::from_secs(2), 3);
        let (rates, _) = window.snapshot(now + Duration::from_secs(2), 3, 0);
        let run = rates.iter().find(|r| r.run == 1).unwrap();
        assert!(run.recent_fill_bytes_per_second.is_none());
        drop(guard);
        for n in 0..100 {
            drop(window.begin(Uuid::new_v4(), 0, now + Duration::from_secs(n + 3), 1));
        }
        assert_eq!(window.inner.lock().unwrap().tracks.len(), MAX_TRACKS);
    }
}
