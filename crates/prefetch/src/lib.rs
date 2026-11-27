//! One bounded warming lane. Durable progress is a hint: every resumed block is verified
//! again. Only strict final-media bytes can enter the shared native cache.
mod attribution;
pub use attribution::{Attribution, BackfillAttribution, PolicyOrigin, ReportedViewer};
mod backfill;
mod demand;
mod lead;
mod rates;
pub use rates::Rate;
mod watched;
use sha2::{Digest as _, Sha256};
use std::collections::HashSet;
use std::fmt::Write as _;
use std::sync::{
    Arc, PoisonError,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use std::time::{Duration, Instant, SystemTime};

use anyhow::{Context as _, bail, ensure};
use bytes::Bytes;
use infinidysk_core::ItemId;
use infinidysk_db::{Db, Recheck, now_ms};
use infinidysk_native_cache::BLOCK;
use infinidysk_vfs::Vfs;
use serde::{Deserialize, Serialize};
use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _};
use tokio::sync::{Mutex, Notify};
use utoipa::ToSchema;
use uuid::Uuid;

const MAX_JOBS: usize = 1000;
const SHARED_WARM_ARTICLES: usize = 8;
const SHARED_WARM_BLOCKS: u64 = 2;
const MAX_RANGES: usize = 64;
const RETRY_MS: i64 = 5 * 60 * 1000;
const EXPIRY_MS: i64 = 7 * 24 * 60 * 60 * 1000;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PrefetchSettings)]
pub struct Settings {
    pub enabled: bool,
    /// Minimum article window under transfer pressure; spare capacity may grow it to 128.
    pub connections_per_job: u8,
    /// Allowance for attempted final-media block fills per UTC day; provider overfetch and wire overhead are excluded.
    pub daily_fill_bytes: u64,
    /// Reserved NNTP response bytes after TLS; greeting/authentication are excluded.
    pub daily_wire_bytes: u64,
    pub plex: PlexSettings,
    pub finish_watched: FinishWatched,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: false,
            connections_per_job: 2,
            daily_fill_bytes: 10_000_000_000,
            daily_wire_bytes: 10_000_000_000,
            plex: PlexSettings::default(),
            finish_watched: FinishWatched::default(),
        }
    }
}
impl Settings {
    pub fn validate(&self) -> anyhow::Result<()> {
        self.plex.validate()?;
        ensure!(
            (1..=8).contains(&self.connections_per_job),
            "warming connection floor must be 1 to 8"
        );
        ensure!(
            (1..=100).contains(&self.finish_watched.percent),
            "finish-watched share must be 1 to 100 percent"
        );
        ensure!(
            (BLOCK..=100_000_000_000_000).contains(&self.daily_wire_bytes),
            "invalid prefetch daily wire-byte allowance"
        );
        ensure!(
            (BLOCK..=100_000_000_000_000).contains(&self.daily_fill_bytes),
            "invalid prefetch daily fill-byte allowance"
        );
        Ok(())
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PlexPrefetchLocalFiles)]
pub enum LocalFiles {
    #[default]
    Disabled,
    ImportedPointers,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PlexPrefetchSettings)]
pub struct PlexSettings {
    pub enabled: bool,
    pub history: bool,
    pub realtime: bool,
    pub local_files: LocalFiles,
    pub next_episodes: u8,
    pub refresh_seconds: u32,
    pub policy: infinidysk_plex::PredictionPolicy,
    pub sources: Vec<infinidysk_plex::sources::Source>,
}
impl Default for PlexSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            history: false,
            realtime: true,
            local_files: LocalFiles::Disabled,
            next_episodes: 2,
            refresh_seconds: 60,
            policy: infinidysk_plex::PredictionPolicy::default(),
            sources: Vec::new(),
        }
    }
}
impl PlexSettings {
    pub fn validate(&self) -> anyhow::Result<()> {
        ensure!(
            (30..=3600).contains(&self.refresh_seconds),
            "Plex refresh must be 30 to 3600 seconds"
        );
        ensure!(
            (1..=20).contains(&self.next_episodes),
            "Plex next-episode limit must be 1 to 20"
        );
        ensure!(self.sources.len() <= 128, "too many Plex warming sources");
        let mut seen = std::collections::HashSet::new();
        for source in &self.sources {
            source.validate()?;
            ensure!(
                seen.insert((&source.server, &source.viewer, source.kind, &source.path)),
                "duplicate Plex warming source"
            );
        }
        self.policy.validate()?;
        Ok(())
    }
}
/// Foreground serving must last at least two minutes and exceed 67 MB before
/// the configured share can admit a full-file completion job.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PrefetchFinishWatched)]
pub struct FinishWatched {
    pub enabled: bool,
    pub percent: u8,
}
impl Default for FinishWatched {
    fn default() -> Self {
        Self {
            enabled: false,
            percent: 10,
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PrefetchFamily)]
pub enum Family {
    #[default]
    Plex,
    Read,
    Backfill,
}
/// Internal admission carries the source revision and configuration that authorized it.
pub struct Automatic<'a> {
    pub item: ItemId,
    pub ranges: &'a [Range],
    pub priority: u8,
    pub intent: Uuid,
    pub revision: &'a str,
    pub scope: &'a str,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PrefetchRange)]
pub struct Range {
    pub start: u64,
    pub end: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PrefetchState)]
pub enum State {
    Queued,
    Paused,
    Complete,
    Cancelled,
    Failed,
    Superseded,
    Expired,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=PrefetchJob)]
pub struct Job {
    pub id: Uuid,
    pub item: Uuid,
    pub revision: String,
    pub size: u64,
    pub ranges: Vec<Range>,
    pub priority: u8,
    /// Opaque intent identity, never a media title, path or token.
    pub intent: Uuid,
    #[serde(default)]
    pub automatic_scope: Option<String>,
    #[serde(default)]
    pub attribution: Attribution,
    #[serde(default)]
    pub family: Family,
    pub state: State,
    pub created_at: i64,
    pub updated_at: i64,
    pub expires_at: i64,
    pub next_attempt: i64,
    pub attempts: u32,
    #[serde(default)]
    pub run: u32,
    pub verified_bytes: u64,
    pub source_bytes: u64,
    pub cached_bytes: u64,
    pub reason: Option<String>,
    range: usize,
    offset: u64,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct Ledger {
    paused: bool,
    day: i64,
    charged: u64,
    jobs: Vec<Job>,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=PrefetchHistory)]
pub struct History {
    pub job: Job,
    pub recorded_at: i64,
}
#[derive(Serialize, ToSchema)]
#[schema(as=PrefetchStatus)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent queue, wire and storage availability observations"
)]
pub struct Status {
    pub paused: bool,
    pub waiting_for_playback: bool,
    pub recovery_mode: bool,
    pub pending_backfills: usize,
    pub rates: Vec<Rate>,
    pub recent_wire_bytes_per_second: Option<f64>,
    pub dropped_backfills: u64,
    pub day: i64,
    pub charged_fill_bytes: u64,
    /// Observed NNTP bytes since process start, including abandoned drains.
    pub received_wire_bytes: u64,
    pub reserved_wire_bytes: Option<u64>,
    pub wire_day: Option<i64>,
    pub wire_blocked: bool,
    pub jobs: Vec<Job>,
    pub history: Vec<History>,
    pub history_available: bool,
    pub state_available: bool,
}
#[derive(Clone, Copy, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PrefetchOperation)]
pub enum Operation {
    Pause,
    Resume,
    Cancel,
    Retry,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum WarmingMode {
    Idle,
    Shared(usize),
    Held,
}

pub struct Queue {
    db: Db,
    vfs: Arc<Vfs>,
    ledger: Mutex<Ledger>,
    /// A step holds this across its I/O. Controls use the ledger only and stay responsive.
    step: Arc<Mutex<()>>,
    automatic_turn: AtomicUsize,
    last_turn_manual: AtomicBool,
    needs_gap_compaction: AtomicBool,
    gap_sweep_cursor: AtomicUsize,
    changed: Notify,
    wire: Arc<infinidysk_nntp::wire::Budget>,
    watched: Mutex<watched::Tracker>,
    lead: Mutex<lead::Lead>,
    /// Sync: [`Self::warming_mode`] runs outside async contexts and never awaits.
    demand: std::sync::Mutex<demand::Demand>,
    uncertain: AtomicBool,
    rates: rates::Window,
}
impl Queue {
    pub async fn load(db: Db, vfs: Arc<Vfs>) -> anyhow::Result<Arc<Self>> {
        {
            let _writing = db.write_permit().await;
            db.ensure_prefetch(&serde_json::to_string(&Ledger::default())?)
                .await?;
        }
        let mut ledger: Ledger = {
            let _writing = db.write_permit().await;
            serde_json::from_str(&db.read_prefetch().await?)?
        };
        ensure!(
            ledger.jobs.len() <= MAX_JOBS,
            "prefetch ledger exceeds job limit"
        );
        for job in &mut ledger.jobs {
            ensure!(
                job.attribution.valid(),
                "invalid persisted warming attribution"
            );
            ensure!(
                job.ranges == normalize(&job.ranges, job.size)?,
                "invalid persisted prefetch ranges"
            );
            ensure!(
                job.revision.len() == 64 && job.revision.bytes().all(|b| b.is_ascii_hexdigit()),
                "invalid persisted source revision"
            );
            if matches!(job.state, State::Queued | State::Paused) {
                // Recheck retained coverage after restart; the cache may have evicted blocks.
                job.range = 0;
                job.offset = job.ranges[0].start;
                job.verified_bytes = 0;
            }
        }
        let wire = if let Some(budget) = vfs.warming_budget() {
            budget.clone()
        } else {
            let db = db.clone();
            let budget = infinidysk_nntp::wire::Budget::new(Arc::new(move |day, bytes, limit| {
                let db = db.clone();
                Box::pin(async move {
                    let _writing = db.write_permit().await;
                    let row = db
                        .reserve_warming_wire(day, bytes, limit)
                        .await
                        .map_err(|_| infinidysk_nntp::wire::Denied)?
                        .ok_or(infinidysk_nntp::wire::Denied)?;
                    u64::try_from(row.granted).map_err(|_| infinidysk_nntp::wire::Denied)
                })
            }));
            ensure!(
                vfs.install_warming_budget(budget.clone()),
                "warming wire admission already installed"
            );
            budget
        };
        wire.pause(ledger.paused);
        let queue = Arc::new(Self {
            db,
            vfs,
            ledger: Mutex::new(ledger),
            step: Arc::new(Mutex::new(())),
            automatic_turn: AtomicUsize::new(0),
            last_turn_manual: AtomicBool::new(false),
            needs_gap_compaction: AtomicBool::new(true),
            gap_sweep_cursor: AtomicUsize::new(0),
            changed: Notify::new(),
            wire,
            watched: Mutex::default(),
            lead: Mutex::default(),
            demand: std::sync::Mutex::default(),
            uncertain: AtomicBool::new(false),
            rates: rates::Window::default(),
        });
        queue
            .rates
            .observe_wire(std::time::Instant::now(), now_ms(), queue.wire.received());
        let weak = Arc::downgrade(&queue);
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_secs(1)).await;
                let Some(queue) = weak.upgrade() else {
                    break;
                };
                queue.rates.observe_wire(
                    std::time::Instant::now(),
                    now_ms(),
                    queue.wire.received(),
                );
            }
        });
        Ok(queue)
    }
    async fn lock_ledger(&self) -> anyhow::Result<tokio::sync::MutexGuard<'_, Ledger>> {
        let mut current = self.ledger.lock().await;
        if self.uncertain.load(Ordering::Acquire) {
            let _writing = self.db.write_permit().await;
            let restored: Ledger = serde_json::from_str(&self.db.read_prefetch().await?)?;
            ensure!(
                restored.jobs.len() <= MAX_JOBS
                    && restored.jobs.iter().all(
                        |j| normalize(&j.ranges, j.size).is_ok_and(|ranges| ranges == j.ranges)
                    ),
                "saved warming state is invalid"
            );
            *current = restored;
            self.wire.pause(current.paused);
            self.uncertain.store(false, Ordering::Release);
        }
        Ok(current)
    }
    // Write before replacing memory. A storage failure never acknowledges a state change.
    async fn save(&self, current: &mut Ledger, next: Ledger) -> anyhow::Result<()> {
        let mut events = Vec::new();
        for job in &next.jobs {
            if !matches!(job.state, State::Queued | State::Paused)
                && current
                    .jobs
                    .iter()
                    .any(|old| old.id == job.id && old.state != job.state)
            {
                let history = History {
                    job: job.clone(),
                    recorded_at: now_ms(),
                };
                let digest = Sha256::digest(serde_json::to_vec(&(job.id, job.run, job.state))?);
                let mut bytes = [0; 16];
                bytes.copy_from_slice(&digest[..16]);
                events.push((
                    Uuid::from_bytes(bytes),
                    history.recorded_at,
                    serde_json::to_string(&history)?,
                ));
            }
        }
        let encode_started = std::time::Instant::now();
        let raw = serde_json::to_string(&next)?;
        let write_started = std::time::Instant::now();
        let _writing = self.db.write_permit().await;
        self.uncertain.store(true, Ordering::Release);
        self.db.save_prefetch(&raw, &events).await?;
        tracing::debug!(
            jobs = next.jobs.len(),
            ledger_bytes = raw.len(),
            encode_ms = write_started.duration_since(encode_started).as_secs_f64() * 1000.0,
            persist_ms = write_started.elapsed().as_secs_f64() * 1000.0,
            "warming ledger saved"
        );
        *current = next;
        self.uncertain.store(false, Ordering::Release);
        self.changed.notify_waiters();
        Ok(())
    }
    pub async fn status(&self) -> Status {
        let wire = self.db.warming_wire_usage().await.ok();
        let history = self.db.prefetch_history(100).await.ok().and_then(|rows| {
            rows.into_iter()
                .map(|raw| serde_json::from_str::<History>(&raw))
                .collect::<Result<Vec<_>, _>>()
                .ok()
        });
        let history_available = history.is_some();
        let ledger = match self.lock_ledger().await {
            Ok(ledger) => ledger,
            Err(_) => self.ledger.lock().await,
        };
        let (mut rates, recent_wire_bytes_per_second) =
            self.rates
                .snapshot(std::time::Instant::now(), now_ms(), self.wire.received());
        rates.retain(|r| {
            r.active
                || !ledger.paused
                    && ledger
                        .jobs
                        .iter()
                        .any(|j| j.id == r.job && j.run == r.run && j.state == State::Queued)
        });
        Status {
            rates,
            recent_wire_bytes_per_second,
            paused: ledger.paused,
            waiting_for_playback: !ledger.paused && self.warming_mode() == WarmingMode::Held,
            pending_backfills: self.vfs.backfills().stats().0,
            dropped_backfills: self.vfs.backfills().stats().1,
            recovery_mode: self
                .vfs
                .native_cache()
                .is_some_and(|cache| cache.recovery_mode()),
            day: ledger.day,
            charged_fill_bytes: ledger.charged,
            received_wire_bytes: self.wire.received(),
            reserved_wire_bytes: wire.as_ref().and_then(|w| u64::try_from(w.charged).ok()),
            wire_day: wire.map(|w| w.day),
            wire_blocked: self.wire.blocked(),
            jobs: ledger.jobs.clone(),
            history: history.unwrap_or_default(),
            history_available,
            state_available: !self.uncertain.load(Ordering::Acquire),
        }
    }
    pub async fn pause(&self, paused: bool) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        next.paused = paused;
        let result = self.save(&mut current, next).await;
        if result.is_ok() {
            self.wire.pause(paused);
            self.rates.forget(None);
        }
        drop(current);
        result
    }
    pub async fn enqueue(
        &self,
        item: ItemId,
        ranges: &[Range],
        priority: u8,
        intent: Uuid,
    ) -> anyhow::Result<Job> {
        self.admit(item, ranges, priority, intent, None).await
    }
    pub async fn enqueue_automatic(&self, request: Automatic<'_>) -> anyhow::Result<Job> {
        self.enqueue_automatic_attributed(request, &Attribution::default())
            .await
    }
    async fn enqueue_automatic_attributed(
        &self,
        request: Automatic<'_>,
        attribution: &Attribution,
    ) -> anyhow::Result<Job> {
        let job = self
            .admit_attributed(
                request.item,
                request.ranges,
                request.priority,
                request.intent,
                Some((request.scope, request.revision, Family::Plex)),
                attribution,
            )
            .await?;
        if job.state == State::Complete {
            let item = self
                .db
                .item(request.item)
                .await?
                .context("prediction source no longer exists")?;
            let key = self.vfs.cache_key(&item).await?;
            ensure!(
                key.revision() == request.revision,
                "prediction source revision changed"
            );
            let cache = self
                .vfs
                .native_cache()
                .context("native cache is unavailable")?;
            let coverage = cache.coverage(&key).await?;
            if !job.ranges.iter().all(|r| {
                coverage
                    .iter()
                    .any(|c| c.start <= r.start && c.end >= r.end)
            }) {
                self.control(job.id, Operation::Retry).await?;
                return self
                    .status()
                    .await
                    .jobs
                    .into_iter()
                    .find(|j| j.id == job.id)
                    .context("prediction job no longer exists");
            }
        }
        Ok(job)
    }
    pub async fn enqueue_automatic_origin(
        &self,
        request: Automatic<'_>,
        origin: PolicyOrigin,
    ) -> anyhow::Result<Job> {
        self.enqueue_automatic_attributed(
            request,
            &Attribution {
                policy: Some(origin),
                ..Attribution::default()
            },
        )
        .await
    }
    #[must_use]
    pub fn pending_backfills(&self) -> usize {
        self.vfs.backfills().stats().0
    }
    /// Scope changes cancel only automatic jobs. A complete new pass can additionally
    /// retire intents no longer predicted; an incomplete pass must never do so.
    pub async fn reconcile_automatic(
        &self,
        scope: Option<&str>,
        wanted: Option<&std::collections::HashSet<Uuid>>,
    ) -> anyhow::Result<()> {
        self.reconcile_family(Family::Plex, scope, wanted).await
    }
    async fn reconcile_family(
        &self,
        family: Family,
        scope: Option<&str>,
        wanted: Option<&std::collections::HashSet<Uuid>>,
    ) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        let mut changed = false;
        for job in &mut next.jobs {
            if let Some(owner) = &job.automatic_scope
                && job.family == family
                && matches!(job.state, State::Queued | State::Paused)
                && (scope != Some(owner.as_str())
                    || wanted.is_some_and(|ids| !ids.contains(&job.intent)))
            {
                job.state = State::Cancelled;
                job.updated_at = now_ms();
                job.reason = Some("Automatic policy no longer requests this job".to_owned());
                changed = true;
            }
        }
        if changed {
            self.save(&mut current, next).await?;
        }
        drop(current);
        Ok(())
    }
    async fn admit(
        &self,
        item: ItemId,
        ranges: &[Range],
        priority: u8,
        intent: Uuid,
        automatic: Option<(&str, &str, Family)>,
    ) -> anyhow::Result<Job> {
        self.admit_attributed(
            item,
            ranges,
            priority,
            intent,
            automatic,
            &Attribution::default(),
        )
        .await
    }
    async fn admit_attributed(
        &self,
        item: ItemId,
        ranges: &[Range],
        priority: u8,
        intent: Uuid,
        automatic: Option<(&str, &str, Family)>,
        attribution: &Attribution,
    ) -> anyhow::Result<Job> {
        ensure!(attribution.valid(), "invalid warming attribution");
        ensure!(priority <= 100, "invalid prefetch priority");
        let item = self
            .db
            .item(item)
            .await?
            .context("prefetch source no longer exists")?;
        let key = self.vfs.cache_key(&item).await?;
        if let Some((scope, revision, _)) = automatic {
            ensure!(
                scope.len() == 64 && scope.bytes().all(|b| b.is_ascii_hexdigit()),
                "invalid automatic policy scope"
            );
            ensure!(
                key.revision() == revision,
                "prediction source revision changed"
            );
        }
        let ranges = normalize(ranges, key.size())?;
        let cache = self
            .vfs
            .native_cache()
            .context("native cache must be available before warming")?;
        ensure!(
            automatic.is_none() || cache.meets_file_minimum(key.size()),
            "source is below the cache minimum"
        );
        let backfill_scope = automatic
            .filter(|(_, _, family)| *family == Family::Backfill)
            .map(|(scope, _, _)| scope);
        // Never edit ranges while the executor owns a selected job.
        let idle_step = backfill_scope.and_then(|_| self.step.try_lock().ok());
        let mut current = self.lock_ledger().await?;
        if let Some(existing) = current.jobs.iter().find(|j| {
            j.item == key.item()
                && j.revision == key.revision()
                && j.intent == intent
                && !(backfill_scope.is_some() && j.state == State::Superseded)
                && (j.ranges == ranges
                    || backfill_scope.is_some_and(|scope| {
                        j.automatic_scope.as_deref() == Some(scope)
                            && ranges.iter().all(|r| {
                                j.ranges
                                    .iter()
                                    .any(|old| old.start <= r.start && old.end >= r.end)
                            })
                    }))
                && (automatic.is_some()
                    || matches!(j.state, State::Queued | State::Paused | State::Complete))
        }) {
            return Ok(existing.clone());
        }
        let mut next = current.clone();
        let now = now_ms();
        if idle_step.is_some()
            && let Some(scope) = backfill_scope
            && let Some((job, changed)) = next.jobs.iter_mut().find_map(|job| {
                if !runnable(job, now)
                    || job.item != key.item()
                    || job.revision != key.revision()
                    || job.automatic_scope.as_deref() != Some(scope)
                    || job.attribution != *attribution
                {
                    return None;
                }
                let merged = merge_pending_gap(job, &ranges)?;
                let changed = merged != job.ranges;
                if changed {
                    job.offset = merged[0].start;
                    job.ranges = merged;
                    job.expires_at = job.expires_at.max(now + EXPIRY_MS);
                }
                Some((job.clone(), changed))
            })
        {
            if changed {
                self.save(&mut current, next).await?;
            }
            return Ok(job);
        }
        let deferred_compaction = backfill_scope.is_some() && idle_step.is_none();
        drop(idle_step);
        // Keep bounded terminal history, never discard a pending job to admit another.
        if next.jobs.len() >= MAX_JOBS
            && let Some(i) = next
                .jobs
                .iter()
                .position(|j| !matches!(j.state, State::Queued | State::Paused))
        {
            next.jobs.remove(i);
        }
        ensure!(next.jobs.len() < MAX_JOBS, "prefetch queue is full");
        let job = Job {
            id: Uuid::new_v4(),
            item: key.item(),
            revision: key.revision().to_owned(),
            size: key.size(),
            offset: ranges[0].start,
            ranges,
            range: 0,
            priority,
            intent,
            automatic_scope: automatic.map(|(scope, _, _)| scope.to_owned()),
            attribution: attribution.clone(),
            family: automatic.map_or(Family::Plex, |(_, _, family)| family),
            state: State::Queued,
            created_at: now,
            updated_at: now,
            expires_at: now + EXPIRY_MS,
            next_attempt: now,
            attempts: 0,
            run: 0,
            verified_bytes: 0,
            source_bytes: 0,
            cached_bytes: 0,
            reason: None,
        };
        next.jobs.push(job.clone());
        self.save(&mut current, next).await?;
        if deferred_compaction {
            self.needs_gap_compaction.store(true, Ordering::Relaxed);
        }
        drop(current);
        Ok(job)
    }
    pub async fn control(&self, id: Uuid, operation: Operation) -> anyhow::Result<()> {
        self.control_attributed(id, operation, None).await
    }
    async fn control_attributed(
        &self,
        id: Uuid,
        operation: Operation,
        attribution: Option<&Attribution>,
    ) -> anyhow::Result<()> {
        ensure!(
            attribution.is_none_or(Attribution::valid),
            "invalid warming attribution"
        );
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        let job = next
            .jobs
            .iter_mut()
            .find(|j| j.id == id)
            .context("prefetch job not found")?;
        match operation {
            Operation::Pause if job.state == State::Queued => job.state = State::Paused,
            Operation::Resume if job.state == State::Paused => job.state = State::Queued,
            Operation::Cancel
                if matches!(job.state, State::Queued | State::Paused | State::Failed) =>
            {
                job.state = State::Cancelled;
            }
            Operation::Retry
                if matches!(job.state, State::Failed | State::Expired | State::Complete) =>
            {
                job.state = State::Queued;
                if let Some(attribution) = attribution {
                    job.attribution = attribution.clone();
                }
                job.run = job
                    .run
                    .checked_add(1)
                    .context("warming attempt counter exhausted")?;
                job.range = 0;
                job.offset = job.ranges[0].start;
                job.verified_bytes = 0;
                job.source_bytes = 0;
                job.cached_bytes = 0;
                job.attempts = 0;
                job.reason = None;
                job.expires_at = now_ms() + EXPIRY_MS;
            }
            _ => bail!("operation does not apply to current prefetch state"),
        }
        job.updated_at = now_ms();
        job.next_attempt = now_ms();
        let result = self.save(&mut current, next).await;
        drop(current);
        if result.is_ok() {
            self.rates.forget(Some(id));
        }
        result
    }
    /// Source-bound completion requests from observed playback. No provider I/O
    /// occurs here; the ordinary durable queue executes and verifies them.
    pub async fn observe_reads(&self, settings: &Settings) -> anyhow::Result<()> {
        let scope = if settings.enabled && settings.finish_watched.enabled {
            Some(
                Sha256::digest(serde_json::to_vec(&settings.finish_watched)?)
                    .iter()
                    .fold(String::with_capacity(64), |mut out, b| {
                        write!(out, "{b:02x}").expect("String formatting is infallible");
                        out
                    }),
            )
        } else {
            None
        };
        self.reconcile_family(Family::Read, scope.as_deref(), None)
            .await?;
        let Some(scope) = scope else {
            *self.watched.lock().await = watched::Tracker::default();
            return Ok(());
        };
        let candidates = self
            .watched
            .lock()
            .await
            .observe(self.vfs.sessions().playback(), now_ms());
        for (id, served) in candidates {
            let Some(item) = self.db.item(id).await? else {
                continue;
            };
            if item.health_state == infinidysk_db::HealthState::Hidden {
                continue;
            }
            let key = self.vfs.cache_key(&item).await?;
            if key.size() == 0
                || self
                    .vfs
                    .native_cache()
                    .is_some_and(|cache| !cache.meets_file_minimum(key.size()))
                || u128::from(served) * 100
                    < u128::from(key.size()) * u128::from(settings.finish_watched.percent)
            {
                continue;
            }
            let mut hash = Sha256::new();
            hash.update(b"finish-watched-v1");
            hash.update(id.0.as_bytes());
            hash.update(key.revision().as_bytes());
            hash.update(scope.as_bytes());
            let digest = hash.finalize();
            let mut bytes = [0; 16];
            bytes.copy_from_slice(&digest[..16]);
            let job = self
                .admit(
                    id,
                    &[Range {
                        start: 0,
                        end: key.size(),
                    }],
                    40,
                    Uuid::from_bytes(bytes),
                    Some((&scope, key.revision(), Family::Read)),
                )
                .await?;
            if job.state == State::Complete {
                let Some(cache) = self.vfs.native_cache() else {
                    continue;
                };
                let coverage = cache.coverage(&key).await?;
                if !coverage.iter().any(|r| r.start == 0 && r.end >= key.size()) {
                    self.control(job.id, Operation::Retry).await?;
                }
            }
        }
        Ok(())
    }
    pub async fn changed(&self) {
        self.changed.notified().await;
    }
    /// Items Plex verified as playing, trusted for `ttl`. Warming keeps yielding to
    /// them even when they read as fast as a bulk transfer.
    pub fn set_verified_playback(&self, items: HashSet<ItemId>, ttl: Duration) {
        let now = Instant::now();
        self.demand
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .set_playing(items, now + ttl);
    }

    fn warming_mode(&self) -> WarmingMode {
        let now = Instant::now();
        let open = self.vfs.sessions().open();
        let any_open = !open.is_empty();
        // Bulk transfers neither hold warming nor make it share: the idle warming
        // window already keeps a quarter of the providers free and drops to its floor
        // while foreground reads wait for them.
        let sessions: Vec<_> = {
            let mut demand = self.demand.lock().unwrap_or_else(PoisonError::into_inner);
            if any_open && demand.due(now) {
                demand.sample(&self.vfs.sessions().playback(), now, SystemTime::now());
            }
            let kept = open
                .into_iter()
                .filter(|s| !demand.bulk(&s.client, s.item, now))
                .collect();
            drop(demand);
            kept
        };
        let only_bulk = any_open && sessions.is_empty();
        let waiting = sessions
            .iter()
            .filter_map(|session| session.waiting())
            .max();
        if waiting.is_some_and(|age| age >= Duration::from_millis(25)) {
            return WarmingMode::Held;
        }
        let demand = waiting.is_some() || (!only_bulk && self.vfs.foreground_pending());
        if demand || !sessions.is_empty() {
            let window = self.vfs.shared_warming_window(SHARED_WARM_ARTICLES);
            if window > 0 {
                return WarmingMode::Shared(window);
            }
        }
        if demand {
            WarmingMode::Held
        } else {
            WarmingMode::Idle
        }
    }

    fn warming_interrupted(&self, started: WarmingMode) -> bool {
        match self.warming_mode() {
            WarmingMode::Held => true,
            // A shared turn has a fixed ceiling. Resize between turns so a
            // transient capacity sample cannot repeatedly cancel the same block.
            WarmingMode::Shared(_) => started == WarmingMode::Idle,
            WarmingMode::Idle => false,
        }
    }
    /// One block at most. The caller owns process cancellation and current settings.
    pub async fn tick(&self, settings: &Settings) -> anyhow::Result<bool> {
        self.tick_bounded(settings, 1).await
    }

    /// Fill a contiguous source window while publishing and checkpointing each block.
    /// Source and wire allowances are reserved before read-ahead; playback and controls
    /// interrupt the window without publishing a partial block.
    pub async fn tick_window(&self, settings: &Settings) -> anyhow::Result<bool> {
        self.tick_bounded(settings, 16).await
    }

    async fn tick_bounded(&self, settings: &Settings, blocks: u64) -> anyhow::Result<bool> {
        let Ok(mut step) = self.step.clone().try_lock_owned() else {
            return Ok(false);
        };
        self.wire
            .configure(settings.enabled, settings.daily_wire_bytes);
        let mode = self.warming_mode();
        if !settings.enabled
            || mode == WarmingMode::Held
            || matches!(mode, WarmingMode::Shared(_)) && !self.vfs.warming_drained()
            || self
                .vfs
                .native_cache()
                .is_some_and(|cache| cache.recovery_mode())
        {
            return Ok(false);
        }
        settings.validate()?;
        let now = now_ms();
        let protected = self.lead.lock().await.protected(&self.db, &self.vfs).await;
        let selected = {
            let mut current = self.lock_ledger().await?;
            if current.paused {
                return Ok(false);
            }
            let mut next = current.clone();
            let mut dirty = false;
            let day = now.div_euclid(86_400_000);
            // Wall-clock rollback must not reset the allowance a second time.
            if day > next.day {
                next.day = day;
                next.charged = 0;
                dirty = true;
            }
            for j in &mut next.jobs {
                if matches!(j.state, State::Queued | State::Paused) && j.expires_at <= now {
                    j.state = State::Expired;
                    j.updated_at = now;
                    dirty = true;
                }
            }
            if let Some(cache) = self.vfs.native_cache() {
                for job in &mut next.jobs {
                    if job.state == State::Queued
                        && job.automatic_scope.is_some()
                        && !cache.meets_file_minimum(job.size)
                    {
                        job.state = State::Superseded;
                        job.reason = Some("Source is below the cache minimum".into());
                        job.updated_at = now;
                        dirty = true;
                    }
                }
            }
            if self.needs_gap_compaction.load(Ordering::Relaxed) {
                dirty |= compact_pending_gaps(&mut next.jobs, now);
            }
            let job = select_job(
                &next.jobs,
                now,
                &protected,
                self.automatic_turn.load(Ordering::Relaxed),
                self.last_turn_manual.load(Ordering::Relaxed),
            )
            .map(|(job, next_turn)| (job.clone(), next_turn));
            if dirty {
                self.save(&mut current, next).await?;
            }
            self.needs_gap_compaction.store(false, Ordering::Relaxed);
            drop(current);
            job
        };
        let Some((mut job, next_turn)) = selected else {
            return Ok(false);
        };
        self.last_turn_manual
            .store(job.automatic_scope.is_none(), Ordering::Relaxed);
        self.automatic_turn.store(next_turn, Ordering::Relaxed);
        let mut rate = self
            .rates
            .begin(job.id, job.run, std::time::Instant::now(), now_ms());
        let source = self.db.item(ItemId(job.item)).await?;
        let key = match source.as_ref() {
            Some(i) => Some(self.vfs.cache_key(i).await?),
            None => None,
        };
        let Some((source, key)) = source
            .zip(key)
            .filter(|(_, k)| k.revision() == job.revision && k.size() == job.size)
        else {
            self.finish(job.id, State::Superseded, "source identity changed")
                .await?;
            return Ok(true);
        };
        let Some(cache) = self.vfs.native_cache() else {
            return Ok(false);
        };
        let Some(lease) = cache.lease(key.clone()) else {
            return Ok(false);
        };
        if untouched_gap(&job)
            && let Ok(coverage) = cache.coverage(&key).await
            && let Some(ranges) = backfill::trim_covered(&job.ranges, &coverage, job.size)
        {
            self.update_unstarted_gap(&job, ranges).await?;
            return Ok(true);
        }
        let block = job.offset / BLOCK;
        if let Some(bytes) = cache.get(&lease, block).await {
            self.checkpoint(&job, bytes.len() as u64, false).await?;
            return Ok(true);
        }
        if !cache.can_fill(&key) {
            let mut current = self.lock_ledger().await?;
            let mut next = current.clone();
            if let Some(j) = next
                .jobs
                .iter_mut()
                .find(|j| j.id == job.id && j.state == State::Queued)
            {
                j.next_attempt = now_ms() + RETRY_MS;
                j.reason = Some("Uncached blocks require an eligible writable cache folder".into());
            }
            self.save(&mut current, next).await?;
            drop(current);
            return Ok(false);
        }
        if self.warming_interrupted(mode) {
            return Ok(false);
        }
        let (shared_window, blocks) = match mode {
            WarmingMode::Shared(window) => (Some(window), blocks.min(SHARED_WARM_BLOCKS)),
            _ => (None, blocks),
        };
        let mut end = job.ranges[job.range]
            .end
            .min(job.offset.saturating_add(blocks * BLOCK));
        if blocks > 1 {
            // A cached block starts a new step: cache hits never consume source allowance.
            let next = job.offset.saturating_add(BLOCK);
            if let Some(cached) = cache
                .coverage(&key)
                .await?
                .iter()
                .filter_map(|r| {
                    let start = r.start.max(next);
                    (start < r.end).then_some(start)
                })
                .min()
            {
                end = end.min(cached);
            }
        }
        let Some(permit) = cache.try_buffer() else {
            return Ok(false);
        };
        // Reserve committed source-byte allowance durably before a source attempt. Failed,
        // interrupted and declined writes remain charged: retries cannot bypass the cap.
        {
            let mut current = self.lock_ledger().await?;
            if current.paused
                || !current
                    .jobs
                    .iter()
                    .any(|j| j.id == job.id && j.state == State::Queued)
            {
                return Ok(false);
            }
            let available = settings.daily_fill_bytes.saturating_sub(current.charged);
            if end - job.offset > available {
                end = job
                    .offset
                    .saturating_add(available / BLOCK * BLOCK)
                    .min(end);
            }
            if end <= job.offset {
                return Ok(false);
            }
            let mut next = current.clone();
            next.charged = next.charged.saturating_add(end - job.offset);
            self.save(&mut current, next).await?;
            drop(current);
        }
        let mut reader = self
            .vfs
            .open_warming(
                &source,
                usize::from(settings.connections_per_job),
                shared_window,
            )
            .await?;
        reader.set_end(end);
        let mut seek = true;
        let mut permit = Some(permit);
        let mut progressed = false;
        while job.offset < end {
            if self.warming_interrupted(mode) {
                return Ok(progressed);
            }
            let length = BLOCK.min(end - job.offset);
            let Some(reserved) = permit.take().or_else(|| cache.try_buffer()) else {
                return Ok(progressed);
            };
            let started = std::time::Instant::now();
            let mut bytes = vec![0; usize::try_from(length)?];
            let transfer = async {
                if seek {
                    reader.seek(std::io::SeekFrom::Start(job.offset)).await?;
                    seek = false;
                }
                reader.read_exact(&mut bytes).await?;
                Ok::<_, std::io::Error>(())
            };
            let interrupted = async {
                loop {
                    tokio::time::sleep(Duration::from_millis(50)).await;
                    if self.warming_interrupted(mode) {
                        break;
                    }
                    let ledger = self.ledger.lock().await;
                    if ledger.paused
                        || !ledger
                            .jobs
                            .iter()
                            .any(|j| j.id == job.id && j.state == State::Queued)
                    {
                        break;
                    }
                }
            };
            let transferred = tokio::select! { result=tokio::time::timeout(Duration::from_secs(30),transfer)=>Some(result), ()=interrupted=>None };
            let Some(transferred) = transferred else {
                return Ok(progressed);
            };
            let source_ms = started.elapsed().as_millis();
            if !matches!(transferred, Ok(Ok(()))) {
                if self.wire.blocked() {
                    return self.defer_wire(&job).await;
                }
                // An inconclusive/circuit failure is urgent work, never proof of permanent damage.
                self.db
                    .request_recheck(ItemId(job.item), Recheck::Playback)
                    .await?;
                self.retry(&job).await?;
                return Ok(true);
            }
            let current = self.db.item(ItemId(job.item)).await?;
            if let Some(current) = current {
                if self.vfs.cache_key(&current).await? != key {
                    self.finish(job.id, State::Superseded, "source identity changed")
                        .await?;
                    return Ok(true);
                }
            } else {
                self.finish(job.id, State::Superseded, "source no longer exists")
                    .await?;
                return Ok(true);
            }
            // Admit publication against pause/cancel, then release the control ledger
            // before storage I/O. The owned write may finish after a pause; checkpoint
            // revalidates controls before acknowledging its durable bytes.
            let ledger = self.ledger.lock().await;
            if ledger.paused
                || !ledger
                    .jobs
                    .iter()
                    .any(|j| j.id == job.id && j.state == State::Queued)
            {
                return Ok(false);
            }
            drop(ledger);
            // The publication owns admission and activity until durability is known,
            // even if a policy change cancels this window. No replacement step can
            // fetch the same block while this write is still finishing.
            let publication_started = std::time::Instant::now();
            let publication = tokio::spawn({
                let cache = cache.clone();
                let lease = lease.clone();
                async move {
                    let published = cache
                        .put_reserved_background(
                            &lease,
                            job.offset / BLOCK,
                            Bytes::from(bytes),
                            reserved,
                        )
                        .await;
                    (published, step, rate)
                }
            });
            let (published, admitted_step, active_rate) = publication.await?;
            step = admitted_step;
            rate = active_rate;
            if matches!(published, Ok(true)) {
                let checkpoint_started = std::time::Instant::now();
                self.checkpoint(&job, length, true).await?;
                tracing::debug!(job=%job.id, offset=job.offset, bytes=length, source_ms,
                    publication_ms=checkpoint_started.duration_since(publication_started).as_millis(),
                    checkpoint_ms=checkpoint_started.elapsed().as_millis(),
                    total_ms=started.elapsed().as_millis(), "warming block committed");
                job.offset += length;
                progressed = true;
            } else {
                let reason = match published {
                    Err(error) => {
                        tracing::warn!(job=%job.id, reason=%std::error::Error::source(&error).unwrap_or(&error), "Warming cache publication failed");
                        "Cache publication failed; check storage diagnostics"
                    }
                    Ok(_) => {
                        "Cache publication declined; check writable cache folders and storage policy"
                    }
                };
                let mut current = self.lock_ledger().await?;
                let mut next = current.clone();
                if let Some(j) = next
                    .jobs
                    .iter_mut()
                    .find(|j| j.id == job.id && j.state == State::Queued)
                {
                    j.next_attempt = now_ms() + RETRY_MS;
                    j.updated_at = now_ms();
                    j.reason = Some(reason.to_owned());
                }
                self.save(&mut current, next).await?;
                drop(current);
                return Ok(progressed);
            }
        }
        drop(reader);
        Ok(progressed)
    }
    async fn defer_wire(&self, job: &Job) -> anyhow::Result<bool> {
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        if let Some(j) = next
            .jobs
            .iter_mut()
            .find(|j| j.id == job.id && j.state == State::Queued)
        {
            j.next_attempt = now_ms() + 60_000;
            j.updated_at = now_ms();
            j.reason = Some("Wire allowance unavailable or exhausted; source unchanged".into());
        }
        self.save(&mut current, next).await?;
        drop(current);
        Ok(false)
    }
    async fn checkpoint(&self, original: &Job, bytes: u64, source: bool) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        if current.paused {
            return Ok(());
        }
        let mut next = current.clone();
        let Some(job) = next.jobs.iter_mut().find(|j| {
            j.id == original.id
                && j.state == State::Queued
                && j.range == original.range
                && j.offset == original.offset
        }) else {
            return Ok(());
        };
        job.offset += bytes;
        job.verified_bytes = job.verified_bytes.saturating_add(bytes);
        if source {
            job.source_bytes = job.source_bytes.saturating_add(bytes);
        } else {
            job.cached_bytes = job.cached_bytes.saturating_add(bytes);
        }
        job.updated_at = now_ms();
        job.attempts = 0;
        job.reason = None;
        if job.offset >= job.ranges[job.range].end {
            job.range += 1;
            if job.range == job.ranges.len() {
                job.state = State::Complete;
            } else {
                job.offset = job.ranges[job.range].start;
            }
        }
        let result = self.save(&mut current, next).await;
        drop(current);
        if result.is_ok() {
            self.rates.record(
                original.id,
                original.run,
                if source { bytes } else { 0 },
                std::time::Instant::now(),
                now_ms(),
            );
        }
        result
    }
    async fn retry(&self, original: &Job) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        if let Some(j) = next
            .jobs
            .iter_mut()
            .find(|j| j.id == original.id && j.state == State::Queued)
        {
            j.attempts = j.attempts.saturating_add(1);
            j.next_attempt = now_ms() + RETRY_MS;
            j.updated_at = now_ms();
            j.reason =
                Some("Source read was inconclusive; urgent health check requested".to_owned());
            if j.attempts >= 3 {
                j.state = State::Failed;
            }
        }
        let result = self.save(&mut current, next).await;
        drop(current);
        result
    }
    async fn finish(&self, id: Uuid, state: State, reason: &str) -> anyhow::Result<()> {
        let mut current = self.lock_ledger().await?;
        let mut next = current.clone();
        if let Some(j) = next
            .jobs
            .iter_mut()
            .find(|j| j.id == id && j.state == State::Queued)
        {
            j.state = state;
            j.reason = Some(reason.to_owned());
            j.updated_at = now_ms();
        }
        let result = self.save(&mut current, next).await;
        drop(current);
        result
    }
}
#[derive(Clone, Copy)]
enum AutomaticLane {
    Playback,
    Gap,
    Prediction,
}

// Four playback, two gap, and one prediction turn per cycle when all lanes are busy.
const AUTOMATIC_TURNS: [AutomaticLane; 7] = [
    AutomaticLane::Playback,
    AutomaticLane::Gap,
    AutomaticLane::Playback,
    AutomaticLane::Prediction,
    AutomaticLane::Playback,
    AutomaticLane::Gap,
    AutomaticLane::Playback,
];

fn automatic_lane(job: &Job) -> AutomaticLane {
    match job.family {
        Family::Backfill => AutomaticLane::Gap,
        Family::Read => AutomaticLane::Playback,
        Family::Plex
            if full_file_warm(job)
                && job
                    .attribution
                    .policy
                    .as_ref()
                    .is_some_and(|origin| origin.source == "playback") =>
        {
            AutomaticLane::Playback
        }
        Family::Plex => AutomaticLane::Prediction,
    }
}

fn select_job<'a>(
    jobs: &'a [Job],
    now: i64,
    protected: &HashSet<ItemId>,
    automatic_turn: usize,
    last_turn_manual: bool,
) -> Option<(&'a Job, usize)> {
    let ready = || jobs.iter().filter(|j| runnable(j, now));
    let rank = |j: &Job| {
        (
            !(full_file_warm(j) || j.family == Family::Backfill)
                || !protected.contains(&ItemId(j.item)),
            std::cmp::Reverse(j.priority),
            j.updated_at,
            j.created_at,
            j.id,
        )
    };
    let manual = ready()
        .filter(|j| j.automatic_scope.is_none())
        .min_by_key(|j| rank(j));
    if let Some(manual) = manual.filter(|_| !last_turn_manual) {
        return Some((manual, automatic_turn));
    }
    let full: HashSet<_> = ready()
        .filter(|j| full_file_warm(j))
        .map(|j| (j.item, j.revision.as_str(), j.size))
        .collect();
    let mut best = [None; 3];
    for job in ready().filter(|j| j.automatic_scope.is_some()) {
        if job.family == Family::Backfill
            && full.contains(&(job.item, job.revision.as_str(), job.size))
        {
            continue;
        }
        let lane = &mut best[automatic_lane(job) as usize];
        if lane.is_none_or(|current| rank(job) < rank(current)) {
            *lane = Some(job);
        }
    }
    for offset in 0..AUTOMATIC_TURNS.len() {
        let turn = (automatic_turn + offset) % AUTOMATIC_TURNS.len();
        if let Some(job) = best[AUTOMATIC_TURNS[turn] as usize] {
            return Some((job, (turn + 1) % AUTOMATIC_TURNS.len()));
        }
    }
    manual.map(|job| (job, automatic_turn))
}

fn runnable(job: &Job, now: i64) -> bool {
    job.state == State::Queued && job.next_attempt <= now && job.expires_at > now
}

fn full_file_warm(job: &Job) -> bool {
    job.family != Family::Backfill
        && job.ranges
            == [Range {
                start: 0,
                end: job.size,
            }]
}

fn untouched_gap(job: &Job) -> bool {
    job.family == Family::Backfill
        && job.state == State::Queued
        && job.run == 0
        && job.verified_bytes == 0
        && job.source_bytes == 0
        && job.cached_bytes == 0
        && job.attempts == 0
        && job.reason.is_none()
        && job.range == 0
        && job
            .ranges
            .first()
            .is_some_and(|first| first.start == job.offset)
}

fn compact_pending_gaps(jobs: &mut [Job], now: i64) -> bool {
    let mut changed = false;
    for index in 1..jobs.len() {
        let (older, rest) = jobs.split_at_mut(index);
        let job = &mut rest[0];
        if !runnable(job, now) || !untouched_gap(job) {
            continue;
        }
        if let Some((target, ranges)) = older.iter_mut().find_map(|target| {
            (runnable(target, now)
                && target.item == job.item
                && target.revision == job.revision
                && target.size == job.size
                && target.automatic_scope == job.automatic_scope
                && target.attribution == job.attribution)
                .then(|| merge_pending_gap(target, &job.ranges))
                .flatten()
                .map(|ranges| (target, ranges))
        }) {
            target.offset = ranges[0].start;
            target.ranges = ranges;
            target.expires_at = target.expires_at.max(job.expires_at);
            job.state = State::Superseded;
            job.reason = Some("Gap ranges consolidated into an existing job".into());
            job.updated_at = now;
            changed = true;
        }
    }
    changed
}

fn merge_pending_gap(job: &Job, incoming: &[Range]) -> Option<Vec<Range>> {
    if !untouched_gap(job) {
        return None;
    }
    let mut combined = job.ranges.clone();
    combined.extend_from_slice(incoming);
    normalize(&combined, job.size).ok()
}

fn normalize(ranges: &[Range], size: u64) -> anyhow::Result<Vec<Range>> {
    ensure!(
        size > 0 && !ranges.is_empty() && ranges.len() <= MAX_RANGES,
        "invalid warming ranges"
    );
    let mut out = Vec::<Range>::new();
    let mut ranges = ranges.to_vec();
    for r in &mut ranges {
        ensure!(
            r.start < r.end && r.end <= size,
            "warming range outside source"
        );
        r.start = r.start / BLOCK * BLOCK;
        r.end = r
            .end
            .checked_next_multiple_of(BLOCK)
            .unwrap_or(size)
            .min(size);
    }
    ranges.sort_by_key(|r| r.start);
    for r in ranges {
        if let Some(last) = out.last_mut().filter(|last| last.end >= r.start) {
            last.end = last.end.max(r.end);
        } else {
            out.push(r);
        }
    }
    Ok(out)
}

#[cfg(test)]
mod durability_tests {
    use super::*;
    #[tokio::test]
    async fn uncertain_control_reads_the_accepted_database_state_before_admitting_more_work() {
        let path = std::env::temp_dir().join(format!(
            "infinidysk-warm-readback-{}.sqlite",
            Uuid::new_v4()
        ));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .expect("database");
        let client = Arc::new(infinidysk_nntp::Client::new([]));
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            client,
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/unused",
        ));
        let queue = Queue::load(db.clone(), vfs).await.expect("queue");
        assert!(!queue.status().await.paused);
        let accepted = Ledger {
            paused: true,
            ..Ledger::default()
        };
        db.save_prefetch(&serde_json::to_string(&accepted).expect("ledger"), &[])
            .await
            .expect("commit whose acknowledgement was lost");
        queue.uncertain.store(true, Ordering::Release);
        let status = queue.status().await;
        assert!(status.state_available);
        assert!(status.paused);
        queue
            .reconcile_automatic(None, None)
            .await
            .expect("reconcile cannot overwrite the observed pause");
        assert!(
            serde_json::from_str::<Ledger>(&db.read_prefetch().await.expect("fenced readback"))
                .expect("ledger")
                .paused
        );
        queue
            .pause(false)
            .await
            .expect("explicit resume after readback");
        assert!(!queue.status().await.paused);
        drop(queue);
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
    #[tokio::test]
    async fn disabling_plex_and_finish_policies_cancels_only_their_own_jobs() {
        let path = std::env::temp_dir().join(format!(
            "infinidysk-warm-families-{}.sqlite",
            Uuid::new_v4()
        ));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .expect("database");
        let make = |family: Family, automatic: bool| Job {
            id: Uuid::new_v4(),
            item: Uuid::new_v4(),
            revision: "a".repeat(64),
            size: 1,
            ranges: vec![Range { start: 0, end: 1 }],
            priority: 40,
            intent: Uuid::new_v4(),
            automatic_scope: automatic.then(|| "b".repeat(64)),
            attribution: Attribution::default(),
            family,
            state: State::Queued,
            created_at: 1,
            updated_at: 1,
            expires_at: i64::MAX,
            next_attempt: 1,
            attempts: 0,
            run: 0,
            verified_bytes: 0,
            source_bytes: 0,
            cached_bytes: 0,
            reason: None,
            range: 0,
            offset: 0,
        };
        let plex = make(Family::Plex, true);
        let read = make(Family::Read, true);
        let manual = make(Family::Plex, false);
        let ledger = Ledger {
            jobs: vec![plex.clone(), read.clone(), manual.clone()],
            ..Ledger::default()
        };
        db.save_prefetch(&serde_json::to_string(&ledger).expect("ledger"), &[])
            .await
            .expect("seed");
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            Arc::new(infinidysk_nntp::Client::new([])),
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/unused",
        ));
        let queue = Queue::load(db.clone(), vfs).await.expect("queue");
        queue
            .reconcile_automatic(None, None)
            .await
            .expect("disable Plex");
        let state = queue.status().await;
        assert_eq!(
            state
                .jobs
                .iter()
                .find(|j| j.id == plex.id)
                .expect("plex")
                .state,
            State::Cancelled
        );
        assert_eq!(
            state
                .jobs
                .iter()
                .find(|j| j.id == read.id)
                .expect("read")
                .state,
            State::Queued
        );
        queue
            .observe_reads(&Settings::default())
            .await
            .expect("disable finish-watched");
        let state = queue.status().await;
        assert_eq!(
            state
                .jobs
                .iter()
                .find(|j| j.id == read.id)
                .expect("read")
                .state,
            State::Cancelled
        );
        assert_eq!(
            state
                .jobs
                .iter()
                .find(|j| j.id == manual.id)
                .expect("manual")
                .state,
            State::Queued
        );
        assert_eq!(state.history.len(), 2);
        drop(queue);
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
}

#[cfg(test)]
mod attribution_history_tests {
    use super::*;
    #[tokio::test]
    async fn automatic_retry_records_new_context_without_rewriting_the_prior_outcome() {
        let root = std::env::temp_dir().join(format!("infinidysk-attribution-{}", Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        let db = Db::connect(&format!("sqlite:{}", root.join("data.sqlite").display()))
            .await
            .unwrap();
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            Arc::new(infinidysk_nntp::Client::new([])),
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/unused",
        ));
        let queue = Queue::load(db.clone(), vfs).await.unwrap();
        let id = Uuid::new_v4();
        let first = Attribution {
            playback: vec![ReportedViewer {
                server: "fixture-server".into(),
                viewer: "fixture-a".into(),
                viewer_name: None,
                player: None,
                duration_ms: 100,
                observed_at: 1,
            }],
            ..Attribution::default()
        };
        let job:Job=serde_json::from_value(serde_json::json!({"id":id,"item":Uuid::new_v4(),"revision":"a".repeat(64),"size":100,"ranges":[{"start":0,"end":100}],"priority":100,"intent":Uuid::new_v4(),"automatic_scope":"b".repeat(64),"family":"backfill","attribution":first,"state":"queued","created_at":1,"updated_at":2,"expires_at":1000,"next_attempt":0,"attempts":0,"verified_bytes":100,"source_bytes":100,"cached_bytes":0,"reason":null,"range":1,"offset":100})).unwrap();
        {
            let mut current = queue.lock_ledger().await.unwrap();
            let mut next = current.clone();
            next.jobs.push(job);
            queue.save(&mut current, next).await.unwrap();
            drop(current);
        }
        queue
            .finish(id, State::Complete, "fixture completion")
            .await
            .unwrap();
        let mut next_context = first.clone();
        next_context.playback[0].viewer = "fixture-b".into();
        next_context.playback[0].observed_at = 3;
        queue
            .control_attributed(id, Operation::Retry, Some(&next_context))
            .await
            .unwrap();
        queue
            .finish(id, State::Failed, "fixture failure")
            .await
            .unwrap();
        let status = queue.status().await;
        let old = status
            .history
            .iter()
            .find(|h| h.job.id == id && h.job.run == 0)
            .unwrap();
        assert_eq!(old.job.attribution, first);
        let new = status
            .history
            .iter()
            .find(|h| h.job.id == id && h.job.run == 1)
            .unwrap();
        assert_eq!(new.job.attribution, next_context);
        drop(queue);
        db.close().await;
        std::fs::remove_dir_all(root).unwrap();
    }
}

#[cfg(test)]
mod scheduling_tests {
    use super::*;

    fn job(family: Family, priority: u8, updated_at: i64) -> Job {
        let size = 4 * BLOCK;
        let end = if family == Family::Backfill {
            BLOCK
        } else {
            size
        };
        Job {
            id: Uuid::new_v4(),
            item: Uuid::new_v4(),
            revision: "a".repeat(64),
            size,
            ranges: vec![Range { start: 0, end }],
            priority,
            intent: Uuid::new_v4(),
            automatic_scope: Some("b".repeat(64)),
            attribution: Attribution::default(),
            family,
            state: State::Queued,
            created_at: updated_at,
            updated_at,
            expires_at: 1000,
            next_attempt: 0,
            attempts: 0,
            run: 0,
            verified_bytes: 0,
            source_bytes: 0,
            cached_bytes: 0,
            reason: None,
            range: 0,
            offset: 0,
        }
    }

    #[test]
    fn automatic_rotation_prefers_playback_without_starving_gaps_or_predictions() {
        let mut jobs = [
            job(Family::Backfill, 100, 1),
            job(Family::Read, 40, 2),
            job(Family::Plex, 90, 3),
        ];
        let protected = HashSet::new();
        let mut turn = 0;
        let order: Vec<_> = (0..AUTOMATIC_TURNS.len())
            .map(|_| {
                let (job, next) = select_job(&jobs, 10, &protected, turn, false).unwrap();
                turn = next;
                job.family
            })
            .collect();
        assert_eq!(
            order,
            [
                Family::Read,
                Family::Backfill,
                Family::Read,
                Family::Plex,
                Family::Read,
                Family::Backfill,
                Family::Read
            ]
        );
        assert_eq!(turn, 0);

        jobs[1].next_attempt = 11;
        assert_eq!(
            select_job(&jobs, 10, &protected, 0, false).map(|(j, _)| j.family),
            Some(Family::Backfill)
        );
        jobs[0].state = State::Paused;
        assert_eq!(
            select_job(&jobs, 10, &protected, 0, false).map(|(j, _)| j.family),
            Some(Family::Plex)
        );
    }

    #[test]
    fn plex_playback_full_file_and_speculative_full_file_are_separate_lanes() {
        let mut jobs = [
            job(Family::Backfill, 100, 1),
            job(Family::Plex, 30, 2),
            job(Family::Plex, 99, 3),
        ];
        jobs[1].attribution.policy = Some(PolicyOrigin {
            source: "playback".into(),
            server: "fixture-server".into(),
            viewer: "fixture-viewer".into(),
            viewer_name: None,
            source_title: None,
            source_path: None,
            observed_at: 1,
        });
        let run = |jobs: &[Job], turn| {
            select_job(jobs, 10, &HashSet::new(), turn, false).map(|(j, _)| j.id)
        };
        assert_eq!(run(&jobs, 0), Some(jobs[1].id));
        assert_eq!(run(&jobs, 1), Some(jobs[0].id));
        assert_eq!(run(&jobs, 3), Some(jobs[2].id));
        jobs[1].ranges[0].end = BLOCK;
        assert_eq!(run(&jobs, 0), Some(jobs[0].id));
    }

    #[test]
    fn manual_work_gets_the_next_turn_and_leaves_a_turn_for_automatic_work() {
        let mut jobs = [
            job(Family::Backfill, 100, 1),
            job(Family::Read, 40, 2),
            job(Family::Plex, 50, 3),
        ];
        jobs[2].automatic_scope = None;
        let protected = HashSet::from([ItemId(jobs[0].item)]);
        let run = |jobs: &[Job], turn, manual| {
            select_job(jobs, 10, &protected, turn, manual).map(|(j, next)| (j.id, next))
        };
        assert_eq!(run(&jobs, 0, false), Some((jobs[2].id, 0)));
        assert_eq!(run(&jobs, 0, true), Some((jobs[1].id, 1)));
        assert_eq!(run(&jobs, 1, true), Some((jobs[0].id, 2)));
        jobs[2].next_attempt = 11;
        assert_eq!(run(&jobs, 0, false), Some((jobs[1].id, 1)));
        jobs[2].next_attempt = 0;
        jobs[0].state = State::Paused;
        jobs[1].state = State::Paused;
        assert_eq!(run(&jobs, 0, true), Some((jobs[2].id, 0)));
    }

    #[test]
    fn gap_falls_back_when_the_matching_full_warm_can_no_longer_run() {
        let mut jobs = [job(Family::Backfill, 100, 1), job(Family::Read, 40, 2)];
        jobs[1].item = jobs[0].item;
        let run = |jobs: &[Job]| select_job(jobs, 10, &HashSet::new(), 1, false).map(|(j, _)| j.id);
        assert_eq!(run(&jobs), Some(jobs[1].id));
        jobs[1].next_attempt = 11;
        assert_eq!(run(&jobs), Some(jobs[0].id));
        jobs[1].next_attempt = 0;
        jobs[1].state = State::Paused;
        assert_eq!(run(&jobs), Some(jobs[0].id));
        jobs[1].state = State::Cancelled;
        assert_eq!(run(&jobs), Some(jobs[0].id));
        jobs[1].state = State::Queued;
        jobs[1].revision = "c".repeat(64);
        assert_eq!(run(&jobs), Some(jobs[0].id));
    }

    #[test]
    fn persisted_gaps_are_compacted_without_losing_disjoint_ranges_or_progress() {
        let mut first = job(Family::Backfill, 100, 1);
        let mut second = job(Family::Backfill, 100, 2);
        second.item = first.item;
        second.ranges[0] = Range {
            start: 2 * BLOCK,
            end: 3 * BLOCK,
        };
        second.offset = 2 * BLOCK;
        let mut started = second.clone();
        started.id = Uuid::new_v4();
        started.verified_bytes = BLOCK;
        first.expires_at = 30;
        second.expires_at = 40;
        let mut jobs = [first, second, started];
        assert!(compact_pending_gaps(&mut jobs, 10));
        assert_eq!(
            jobs[0].ranges,
            vec![
                Range {
                    start: 0,
                    end: BLOCK
                },
                Range {
                    start: 2 * BLOCK,
                    end: 3 * BLOCK
                }
            ]
        );
        assert_eq!(jobs[0].expires_at, 40);
        assert_eq!(jobs[1].state, State::Superseded);
        assert_eq!(jobs[2].state, State::Queued);
        assert!(!compact_pending_gaps(&mut jobs, 10));
    }

    #[test]
    fn coalescing_preserves_controls_progress_and_disjoint_gaps() {
        let mut job = job(Family::Backfill, 100, 1);
        let touching = [Range {
            start: BLOCK,
            end: 2 * BLOCK,
        }];
        let disjoint = [Range {
            start: 2 * BLOCK,
            end: 3 * BLOCK,
        }];
        assert_eq!(
            merge_pending_gap(&job, &touching),
            Some(vec![Range {
                start: 0,
                end: 2 * BLOCK
            }])
        );
        assert_eq!(
            merge_pending_gap(&job, &disjoint),
            Some(vec![job.ranges[0].clone(), disjoint[0].clone()])
        );
        job.state = State::Paused;
        assert!(merge_pending_gap(&job, &touching).is_none());
        job.state = State::Queued;
        job.verified_bytes = BLOCK;
        assert!(merge_pending_gap(&job, &touching).is_none());
    }
}
