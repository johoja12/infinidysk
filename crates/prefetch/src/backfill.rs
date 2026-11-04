//! Playback miss hints carry exact served ranges and a source revision. They are
//! hints until persisted; coverage is checked again by the normal strict executor.
use super::*;
impl Queue {
    pub async fn observe_backfills(&self, settings: &Settings) -> anyhow::Result<()> {
        let scope = settings
            .enabled
            .then_some("a56f8f03b8bd3d3866df663eaa504efe3f08e0c7c9b1992e91b6b130e17d82fa");
        self.reconcile_family(Family::Backfill, scope, None).await?;
        let Some(scope) = scope else {
            return Ok(());
        };
        let Some(cache) = self.vfs.native_cache().filter(|c| !c.recovery_mode()) else {
            return Ok(());
        };
        let journal = self.vfs.backfills();
        for hint in journal.pending() {
            let Some(item) = self.db.item(hint.item).await? else {
                journal.acknowledge(hint.id);
                continue;
            };
            if !matches!(
                item.health_state,
                infinidysk_db::HealthState::Unchecked | infinidysk_db::HealthState::Healthy
            ) {
                journal.acknowledge(hint.id);
                continue;
            }
            let key = self.vfs.cache_key(&item).await?;
            if key.revision() != hint.revision || key.size() != hint.size {
                journal.acknowledge(hint.id);
                continue;
            }
            let coverage = cache.coverage(&key).await?;
            let ranges = missing(hint.start, hint.end, &coverage);
            if !ranges.is_empty() {
                let mut hash = Sha256::new();
                hash.update(b"native-backfill-v1");
                hash.update(item.id.0.as_bytes());
                hash.update(key.revision().as_bytes());
                hash.update(hint.start.to_le_bytes());
                hash.update(hint.end.to_le_bytes());
                let mut bytes = [0; 16];
                bytes.copy_from_slice(&hash.finalize()[..16]);
                let job = self
                    .admit(
                        item.id,
                        &ranges,
                        100,
                        Uuid::from_bytes(bytes),
                        Some((scope, key.revision(), Family::Backfill)),
                    )
                    .await?;
                if matches!(job.state, State::Complete | State::Expired) {
                    self.control(job.id, Operation::Retry).await?;
                }
            }
            // If coalescing replaced the event during database I/O, its newer nonce
            // remains pending. An uncertain durable write never acknowledges here.
            journal.acknowledge(hint.id);
        }
        Ok(())
    }
}
fn missing(start: u64, end: u64, coverage: &[infinidysk_native_cache::Coverage]) -> Vec<Range> {
    let mut at = start;
    let mut ranges = Vec::new();
    for covered in coverage {
        if covered.end <= at {
            continue;
        }
        if covered.start >= end {
            break;
        }
        if covered.start > at {
            ranges.push(Range {
                start: at,
                end: covered.start.min(end),
            });
        }
        at = at.max(covered.end).min(end);
    }
    if at < end {
        ranges.push(Range { start: at, end });
    }
    // A bounded intent can include covered blocks rather than silently omit any
    // missing interval. Cache hits consume neither source nor wire allowance.
    if ranges.len() > MAX_RANGES {
        vec![Range { start, end }]
    } else {
        ranges
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_native_cache::Coverage;
    #[test]
    fn subtracts_coverage_without_inventing_a_position_or_skipping_a_hole() {
        assert_eq!(
            missing(
                0,
                10,
                &[Coverage { start: 2, end: 4 }, Coverage { start: 6, end: 9 }]
            ),
            vec![
                Range { start: 0, end: 2 },
                Range { start: 4, end: 6 },
                Range { start: 9, end: 10 }
            ]
        );
        assert_eq!(missing(2, 4, &[Coverage { start: 0, end: 10 }]), Vec::new());
    }
}
