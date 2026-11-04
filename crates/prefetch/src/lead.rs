//! Conservative cached lead for every foreground reader, including handle gaps.
//! Positions are evidence of where to retain bytes, never watched-progress evidence.
use infinidysk_core::ItemId;
use infinidysk_db::{Db, now_ms};
use infinidysk_native_cache::Coverage;
use infinidysk_vfs::Vfs;
use std::collections::{HashMap, HashSet};
use std::time::{SystemTime, UNIX_EPOCH};
const RATE: u64 = 12_500_000;
const MINIMUM_MS: u64 = 600_000;
const RETAIN_MS: i64 = 120_000;
const CHECK_MS: i64 = 5_000;
#[derive(Default)]
pub(super) struct Lead {
    checked: Option<i64>,
    protected: HashSet<ItemId>,
}
fn ms(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}
fn short(position: Option<u64>, seen: i64, now: i64, size: u64, ranges: &[Coverage]) -> bool {
    let Some(position) = position.filter(|p| *p <= size) else {
        return true;
    };
    let end = ranges
        .iter()
        .find(|r| r.start <= position && r.end > position)
        .map_or(position, |r| r.end.min(size));
    if end == size {
        return false;
    }
    let age = now.saturating_sub(seen).cast_unsigned();
    let needed = RATE.saturating_mul(MINIMUM_MS.saturating_add(age)) / 1000;
    end.saturating_sub(position) < needed
}
impl Lead {
    pub(super) async fn protected(&mut self, db: &Db, vfs: &Vfs) -> HashSet<ItemId> {
        let now = now_ms();
        if self
            .checked
            .is_some_and(|last| now >= last && now - last < CHECK_MS)
        {
            return self.protected.clone();
        }
        self.checked = Some(now);
        self.protected.clear();
        let reads = vfs.sessions().playback();
        // An incomplete snapshot cannot justify yielding a watched file. On overflow
        // protect every observed item, without NAS reads.
        let mut candidates: HashMap<ItemId, Vec<(Option<u64>, i64)>> = HashMap::new();
        for r in reads {
            let seen = ms(r.closed_at.unwrap_or_else(SystemTime::now));
            if r.closed_at.is_some() && now.saturating_sub(seen) > RETAIN_MS {
                continue;
            }
            candidates
                .entry(r.item)
                .or_default()
                .push(((r.served > 0).then_some(r.position), seen));
        }
        if candidates.len() > 32 || candidates.values().map(Vec::len).sum::<usize>() > 2048 {
            self.protected.extend(candidates.keys().copied());
            return self.protected.clone();
        }
        let Some(cache) = vfs.native_cache() else {
            return HashSet::new();
        };
        for (id, readers) in candidates {
            let coverage = async {
                let item = db.item(id).await.ok().flatten()?;
                let key = vfs.cache_key(&item).await.ok()?;
                let ranges = cache.coverage(&key).await.ok()?;
                Some((key.size(), ranges))
            }
            .await;
            if coverage.is_none_or(|(size, ranges)| {
                readers
                    .iter()
                    .any(|(position, seen)| short(*position, *seen, now, size, &ranges))
            }) {
                self.protected.insert(id);
            }
        }
        self.protected.clone()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cached_lead_ages_across_bursts_and_unknown_positions_stay_protected() {
        let ranges = vec![Coverage {
            start: 0,
            end: RATE * 660,
        }];
        let size = RATE * 1800;
        assert!(!short(Some(0), 1000, 1000, size, &ranges));
        assert!(short(Some(0), 1000, 62001, size, &ranges));
        assert!(short(None, 1000, 1000, size, &ranges));
        assert!(short(Some(size + 1), 1000, 1000, size, &ranges));
        assert!(short(Some(RATE * 300), 1000, 1000, size, &ranges));
        assert!(!short(
            Some(size - 1),
            1000,
            1000,
            size,
            &[Coverage {
                start: size - 10,
                end: size
            }]
        ));
        assert!(short(
            Some(0),
            1000,
            1000,
            size,
            &[Coverage {
                start: 1,
                end: size
            }]
        ));
    }
}
