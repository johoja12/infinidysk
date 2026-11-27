//! Which open readers warming yields to.
//!
//! Warming yields to a reader that waits for data. A reader that pulls one file much
//! faster than any media plays (a copy, a full-file client cache fill, a scan) is a
//! bulk transfer: warming shares the providers with it instead of yielding. An item
//! Plex reports as playing is never bulk, whatever its read rate, because a Plex
//! server behind a full-file client cache also reads at full speed.
use infinidysk_core::ItemId;
use infinidysk_vfs::Playback;
use std::collections::{HashMap, HashSet, VecDeque};
use std::time::{Duration, Instant, SystemTime};

/// Highest media bitrate playback needs (100 Mbit/s), the pace `lead` also assumes.
const PLAYBACK_BYTES_PER_SECOND: u64 = 12_500_000;
/// A reader becomes bulk after it averages more than the playback pace over a full
/// window, and stops being bulk once it averages less than half of it.
const WINDOW: Duration = Duration::from_secs(30);
/// Snapshots closer together than this reuse the last classification.
const SAMPLE: Duration = Duration::from_millis(500);

/// One client reading one item. Clients such as rclone reopen a file every chunk, so
/// a reader spans many sessions.
type Key = (String, ItemId);

struct Reader {
    since: Instant,
    /// Bytes served between two snapshots, stamped with the later one.
    samples: VecDeque<(Instant, u64)>,
    bulk: bool,
}

impl Reader {
    const fn new(now: Instant) -> Self {
        Self {
            since: now,
            samples: VecDeque::new(),
            bulk: false,
        }
    }

    fn classify(&mut self, now: Instant) {
        let start = now.checked_sub(WINDOW).unwrap_or(self.since);
        while self.samples.front().is_some_and(|(at, _)| *at <= start) {
            self.samples.pop_front();
        }
        let bytes: u64 = self.samples.iter().map(|(_, n)| n).sum();
        let pace = PLAYBACK_BYTES_PER_SECOND.saturating_mul(WINDOW.as_secs());
        if self.bulk {
            self.bulk = bytes >= pace / 2;
        } else {
            self.bulk = self.since <= start && bytes > pace;
        }
    }
}

#[derive(Default)]
pub(super) struct Demand {
    /// The last snapshot, by monotonic and wall clock.
    sampled: Option<(Instant, SystemTime)>,
    /// Served bytes per session id at the last snapshot.
    served: HashMap<u64, u64>,
    readers: HashMap<Key, Reader>,
    playing: HashSet<ItemId>,
    playing_until: Option<Instant>,
}

impl Demand {
    /// Items Plex verified as playing, trusted until `until`.
    pub(super) fn set_playing(&mut self, items: HashSet<ItemId>, until: Instant) {
        self.playing = items;
        self.playing_until = Some(until);
    }

    fn playing(&self, item: ItemId, now: Instant) -> bool {
        self.playing_until.is_some_and(|until| now < until) && self.playing.contains(&item)
    }

    /// Whether `client` reading `item` is a bulk transfer warming need not yield to.
    pub(super) fn bulk(&self, client: &str, item: ItemId, now: Instant) -> bool {
        !self.playing(item, now)
            && self
                .readers
                .get(&(client.to_owned(), item))
                .is_some_and(|r| r.bulk)
    }

    pub(super) fn due(&self, now: Instant) -> bool {
        self.sampled
            .is_none_or(|(at, _)| now.saturating_duration_since(at) >= SAMPLE)
    }

    /// Takes a snapshot of open and recently closed reads.
    pub(super) fn sample(&mut self, reads: &[Playback], now: Instant, wall: SystemTime) {
        // Bytes from a gap longer than the window would land in a single sample and
        // read as a burst. Start over instead.
        let previous = self
            .sampled
            .filter(|(at, _)| now.saturating_duration_since(*at) <= WINDOW)
            .map(|(_, wall)| wall);
        if previous.is_none() {
            self.readers.clear();
        }
        let mut served = HashMap::with_capacity(reads.len());
        let mut present = HashSet::with_capacity(reads.len());
        for read in reads {
            let base = match self.served.get(&read.id) {
                Some(base) if previous.is_some() => *base,
                // A session that opened before the last snapshot served those bytes
                // at an unknown time: they are not evidence of a recent rate.
                _ if previous.is_none_or(|at| read.opened_at < at) => read.served,
                _ => 0,
            };
            served.insert(read.id, read.served);
            let key = (read.client.clone(), read.item);
            let reader = self
                .readers
                .entry(key.clone())
                .or_insert_with(|| Reader::new(now));
            let bytes = read.served.saturating_sub(base);
            if bytes > 0 {
                reader.samples.push_back((now, bytes));
            }
            present.insert(key);
        }
        self.served = served;
        self.readers.retain(|key, reader| {
            reader.classify(now);
            present.contains(key) || !reader.samples.is_empty()
        });
        self.sampled = Some((now, wall));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_vfs::Delivery;
    use uuid::Uuid;

    const MB: u64 = 1_000_000;

    struct Clock {
        start: Instant,
        wall: SystemTime,
    }

    impl Clock {
        fn new() -> Self {
            Self {
                start: Instant::now(),
                wall: SystemTime::UNIX_EPOCH + Duration::from_hours(500_000),
            }
        }
        fn at(&self, ms: u64) -> (Instant, SystemTime) {
            let d = Duration::from_millis(ms);
            (self.start + d, self.wall + d)
        }
    }

    fn read(id: u64, client: &str, item: ItemId, opened: SystemTime, served: u64) -> Playback {
        Playback {
            id,
            item,
            delivery: Delivery::WebDav,
            client: client.to_owned(),
            player: None,
            opened_at: opened,
            closed_at: None,
            served,
            native_cache_bytes: 0,
            position: served,
        }
    }

    /// Feeds one reader that serves `rate` bytes per second and reopens its session
    /// every 10 s, sampled once a second from second `from` to `to`.
    fn run(demand: &mut Demand, clock: &Clock, item: ItemId, rate: u64, from: u64, to: u64) {
        let reopen_s = 10;
        for s in from..=to {
            let (now, wall) = clock.at(s * 1000);
            let session = s / reopen_s;
            let (_, opened) = clock.at(session * reopen_s * 1000);
            let served = (s - session * reopen_s) * rate;
            demand.sample(
                &[read(session + 1, "copier", item, opened, served)],
                now,
                wall,
            );
        }
    }

    #[test]
    fn a_reader_far_above_playback_pace_becomes_bulk_only_after_a_full_window() {
        let clock = Clock::new();
        let item = ItemId(Uuid::from_u128(1));
        let mut demand = Demand::default();
        run(&mut demand, &clock, item, 25 * MB, 0, 20);
        let (now, _) = clock.at(20_000);
        assert!(
            !demand.bulk("copier", item, now),
            "a short burst is buffering"
        );
        run(&mut demand, &clock, item, 25 * MB, 21, 40);
        let (now, _) = clock.at(40_000);
        assert!(demand.bulk("copier", item, now));
        assert!(!demand.bulk("other", item, now), "bulk is per client");
    }

    #[test]
    fn a_reader_at_playback_pace_is_never_bulk() {
        let clock = Clock::new();
        let item = ItemId(Uuid::from_u128(2));
        let mut demand = Demand::default();
        run(&mut demand, &clock, item, 12 * MB, 0, 120);
        let (now, _) = clock.at(120_000);
        assert!(!demand.bulk("copier", item, now));
    }

    #[test]
    fn plex_verified_playback_is_never_bulk_until_the_verification_expires() {
        let clock = Clock::new();
        let item = ItemId(Uuid::from_u128(3));
        let mut demand = Demand::default();
        let (now, _) = clock.at(0);
        demand.set_playing(HashSet::from([item]), now + Duration::from_secs(60));
        run(&mut demand, &clock, item, 40 * MB, 0, 50);
        let (now, _) = clock.at(50_000);
        assert!(!demand.bulk("copier", item, now));
        let (later, _) = clock.at(61_000);
        assert!(demand.bulk("copier", item, later));
    }

    #[test]
    fn bytes_served_before_tracking_or_across_a_long_gap_are_not_a_burst() {
        let clock = Clock::new();
        let item = ItemId(Uuid::from_u128(4));
        let mut demand = Demand::default();
        let (_, opened) = clock.at(0);
        // An old session with 50 GB already served, then slow reads.
        for s in 0..=40 {
            let (now, wall) = clock.at(1_000_000 + s * 1000);
            let served = 50_000 * MB + s * MB;
            demand.sample(&[read(9, "copier", item, opened, served)], now, wall);
        }
        let (now, _) = clock.at(1_040_000);
        assert!(!demand.bulk("copier", item, now));
        // A two-minute gap with 10 GB more served must not read as a burst either.
        let (later, later_wall) = clock.at(1_160_000);
        demand.sample(
            &[read(9, "copier", item, opened, 60_040 * MB)],
            later,
            later_wall,
        );
        assert!(!demand.bulk("copier", item, later));
    }

    #[test]
    fn a_bulk_reader_stays_bulk_until_it_drops_below_half_the_pace() {
        let clock = Clock::new();
        let item = ItemId(Uuid::from_u128(5));
        let mut demand = Demand::default();
        let (_, opened) = clock.at(0);
        let mut served = 0;
        let mut sample = |demand: &mut Demand, s: u64, rate: u64| {
            served += rate;
            let (now, wall) = clock.at(s * 1000);
            demand.sample(&[read(1, "copier", item, opened, served)], now, wall);
            now
        };
        for s in 1..=40 {
            sample(&mut demand, s, 25 * MB);
        }
        // Warming takes some capacity: the copy slows to 8 MB/s, still above half pace.
        let mut now = Instant::now();
        for s in 41..=80 {
            now = sample(&mut demand, s, 8 * MB);
        }
        assert!(demand.bulk("copier", item, now));
        for s in 81..=120 {
            now = sample(&mut demand, s, 2 * MB);
        }
        assert!(!demand.bulk("copier", item, now));
    }
}
