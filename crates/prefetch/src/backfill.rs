//! Playback miss hints carry exact served ranges and a source revision. They are
//! hints until persisted; coverage is checked again by the normal strict executor.
use super::*;
impl Queue {
    pub async fn observe_backfills(&self, settings: &Settings) -> anyhow::Result<()> {
        self.observe_backfills_attributed(settings, &[]).await
    }
    pub async fn observe_backfills_attributed(
        &self,
        settings: &Settings,
        contexts: &[BackfillAttribution],
    ) -> anyhow::Result<()> {
        ensure!(
            contexts.len() <= 32,
            "too many backfill attribution sources"
        );
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
            if key.revision() != hint.revision
                || key.size() != hint.size
                || !cache.meets_file_minimum(key.size())
            {
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
                let now = now_ms();
                let attribution = contexts
                    .iter()
                    .find(|c| c.item == hint.item.0 && c.revision == hint.revision)
                    .map(|c| &c.attribution)
                    .filter(|a| {
                        a.valid()
                            && a.playback
                                .iter()
                                .all(|p| now >= p.observed_at && now - p.observed_at <= 120_000)
                    })
                    .cloned()
                    .unwrap_or_default();
                let job = self
                    .admit_attributed(
                        item.id,
                        &ranges,
                        100,
                        Uuid::from_bytes(bytes),
                        Some((scope, key.revision(), Family::Backfill)),
                        &attribution,
                    )
                    .await?;
                if matches!(job.state, State::Complete | State::Expired) {
                    self.control_attributed(job.id, Operation::Retry, Some(&attribution))
                        .await?;
                }
            }
            // If coalescing replaced the event during database I/O, its newer nonce
            // remains pending. An uncertain durable write never acknowledges here.
            journal.acknowledge(hint.id);
        }
        Ok(())
    }
    /// Reconcile a rotating batch independently of download admission. Already
    /// covered jobs may be waiting behind playback or a full-file warming job.
    pub async fn reconcile_cached_gaps(&self) -> anyhow::Result<()> {
        const BATCH: usize = 8;
        let Some(cache) = self.vfs.native_cache().filter(|c| !c.recovery_mode()) else {
            return Ok(());
        };
        let batch = {
            let ledger = self.lock_ledger().await?;
            if ledger.paused {
                return Ok(());
            }
            let now = now_ms();
            let jobs: Vec<_> = ledger
                .jobs
                .iter()
                .filter(|job| untouched_gap(job) && runnable(job, now))
                .collect();
            if jobs.is_empty() {
                return Ok(());
            }
            let start = self.gap_sweep_cursor.fetch_add(BATCH, Ordering::Relaxed) % jobs.len();
            let batch = jobs
                .iter()
                .cycle()
                .skip(start)
                .take(BATCH.min(jobs.len()))
                .map(|job| (*job).clone())
                .collect::<Vec<_>>();
            drop(ledger);
            batch
        };
        let mut updates = Vec::new();
        for job in batch {
            let Some(item) = self.db.item(ItemId(job.item)).await? else {
                continue;
            };
            let key = self.vfs.cache_key(&item).await?;
            if key.revision() == job.revision
                && key.size() == job.size
                && let Some(ranges) =
                    trim_covered(&job.ranges, &cache.native_coverage(&key).await?, job.size)
            {
                updates.push((job, ranges));
            }
        }
        if updates.is_empty() {
            return Ok(());
        }
        let Ok(_step) = self.step.try_lock() else {
            return Ok(());
        };
        let mut current = self.lock_ledger().await?;
        if current.paused {
            return Ok(());
        }
        let mut next = current.clone();
        let mut changed = false;
        let now = now_ms();
        for (original, ranges) in updates {
            if let Some(job) = next.jobs.iter_mut().find(|job| {
                job.id == original.id
                    && untouched_gap(job)
                    && runnable(job, now)
                    && job.ranges == original.ranges
            }) {
                apply_unstarted_gap(job, ranges, now);
                changed = true;
            }
        }
        if changed {
            self.save(&mut current, next).await?;
        }
        drop(current);
        Ok(())
    }

    pub(super) async fn update_unstarted_gap(
        &self,
        original: &Job,
        ranges: Vec<Range>,
    ) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        if current.paused {
            return Ok(());
        }
        let mut next = current.clone();
        let Some(job) = next
            .jobs
            .iter_mut()
            .find(|j| j.id == original.id && untouched_gap(j))
        else {
            return Ok(());
        };
        apply_unstarted_gap(job, ranges, now_ms());
        let result = self.save(&mut current, next).await;
        drop(current);
        result
    }
}
fn apply_unstarted_gap(job: &mut Job, ranges: Vec<Range>, now: i64) {
    if let Some(first) = ranges.first() {
        job.offset = first.start;
        job.ranges = ranges;
    } else {
        job.state = State::Superseded;
        job.reason = Some("Gap ranges already covered by cache".into());
    }
    job.updated_at = now;
}

pub(super) fn trim_covered(
    ranges: &[Range],
    coverage: &[infinidysk_native_cache::Coverage],
    size: u64,
) -> Option<Vec<Range>> {
    let remaining: Vec<_> = ranges
        .iter()
        .flat_map(|r| missing(r.start, r.end, coverage))
        .collect();
    if remaining.is_empty() {
        return Some(remaining);
    }
    let remaining = normalize(&remaining, size).ok()?;
    (remaining != ranges).then_some(remaining)
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
    fn coverage_trim_keeps_only_whole_missing_blocks() {
        let ranges = [Range {
            start: 0,
            end: 3 * BLOCK,
        }];
        assert_eq!(
            trim_covered(
                &ranges,
                &[Coverage {
                    start: 0,
                    end: 3 * BLOCK
                }],
                3 * BLOCK
            ),
            Some(vec![])
        );
        assert_eq!(
            trim_covered(
                &ranges,
                &[Coverage {
                    start: 0,
                    end: BLOCK + 1
                }],
                3 * BLOCK
            ),
            Some(vec![Range {
                start: BLOCK,
                end: 3 * BLOCK
            }])
        );
        assert_eq!(
            trim_covered(
                &ranges,
                &[Coverage {
                    start: 1,
                    end: BLOCK - 1
                }],
                3 * BLOCK
            ),
            None
        );
    }
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
