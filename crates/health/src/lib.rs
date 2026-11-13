//! Health monitoring and repair as specified in `infinidysk-lab:docs/health-policy.md`: check
//! stored files on an age-based schedule, confirm misses twice, and get broken files replaced
//! through the Arr that imported them.

mod check;
mod legacy;
pub mod par2;
mod regrab;
pub use regrab::Control as RegrabControl;
mod structure;
pub mod sweep;

use std::collections::HashMap;
use std::panic::AssertUnwindSafe;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use futures_util::FutureExt as _;
use futures_util::future::BoxFuture;
use infinidysk_arr as arr;
use infinidysk_core::{BlobId, FileMeta, ItemId, JobId, blob, media};
use infinidysk_db::{
    Db, Defect, FirstCheck, HealthCheck, HealthResult, HealthState, HealthStatus, Item, MediaProbe,
    ProbeAttempt, ProbeOutcome, Recheck, Repair, now_ms,
};
use infinidysk_nntp::{Articles, Client, RepairRevision};
use sha2::{Digest as _, Sha256};
use tokio::sync::{Notify, OwnedRwLockReadGuard, watch};

use check::Outcome;
pub use infinidysk_arr::{Arr, Profile};
use structure::Verdict;

/// Lets the mount and the first streams settle before checks compete for connections.
const STARTUP_DELAY: Duration = Duration::from_secs(20);
const IDLE_POLL: Duration = Duration::from_secs(60);
const MINUTE_MS: i64 = 60 * 1000;
const HOUR_MS: i64 = 60 * MINUTE_MS;
const DAY_MS: i64 = 24 * HOUR_MS;
const SUSPECT_RECHECK_MS: i64 = 15 * MINUTE_MS;
const INCONCLUSIVE_RECHECK_MS: i64 = 30 * MINUTE_MS;
const CADENCE_CAP_MS: i64 = 90 * DAY_MS;
/// Consecutive inconclusive checks of one file before a warning.
const INCONCLUSIVE_WARN: u32 = 3;
/// Probes in flight per file.
const CONCURRENCY_IDLE: usize = 16;
const CONCURRENCY_STREAMING: usize = 4;
const ARR_WAIT: Duration = Duration::from_secs(120);
const HIDDEN_WAIT_MS: i64 = DAY_MS;
const ARR_RETRY_MS: i64 = HOUR_MS;
/// Retry delay for a repair held back by the shared search limit.
const SEARCH_WAIT_MS: i64 = infinidysk_db::SEARCH_WINDOW_MS;
const REPAIR_BUDGET: i16 = 3;
const REJECT_FOR_MS: i64 = 14 * DAY_MS;
const REJECT_IDS: usize = 200;
/// Identical `action_needed` results before checks go weekly.
const ACTION_NEEDED_SETTLE: i64 = 3;
const RETENTION_MS: i64 = 30 * DAY_MS;
/// Config key set once every enabled Arr was reconciled, so its owner backfill and link
/// adoption ran.
pub const BACKFILLED_KEY: &str = "reconcile.linked_at";

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Regrab(&'static str),
    #[error("regrab journal is unreadable")]
    Journal(#[from] serde_json::Error),
    #[error("health storage failed")]
    Db(#[from] infinidysk_db::Error),
    #[error("stored metadata is unreadable")]
    Meta(#[from] infinidysk_core::blob::Error),
    #[error("HTTP client setup failed")]
    Http(#[from] reqwest::Error),
}

/// The user-settable choices of §10.
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent user toggles, mirrored from settings"
)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Settings {
    /// Run routine checks; rechecks and repairs run regardless.
    pub enabled: bool,
    /// Share of segments a check probes, 1 to 100.
    pub sample_percent: u8,
    pub sample_depth: media::SampleDepth,
    pub auto_repair: bool,
    pub degraded_tolerance: bool,
    pub unowned_action: UnownedAction,
    /// Analyze good files' media automatically once.
    pub media_probe: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: true,
            sample_percent: media::SAMPLE_PERCENT,
            sample_depth: media::SampleDepth::Percent,
            auto_repair: true,
            degraded_tolerance: true,
            unowned_action: UnownedAction::Delete,
            media_probe: false,
        }
    }
}

/// What happens to a broken file no Arr imported.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum UnownedAction {
    #[default]
    Delete,
    ActionNeeded,
}

type Gate = Box<dyn Fn() -> bool + Send + Sync>;
/// The write permit of one health step; `None` while it waits on an Arr or a provider.
type Writing = Option<OwnedRwLockReadGuard<()>>;

/// What one media analysis of a file found.
#[derive(Debug)]
pub enum Analysis {
    /// Metadata JSON, carrying its own schema version.
    Discovered(String),
    NoStreams,
    /// The analyzer does not read this container; why.
    Unsupported(String),
    /// The attempt proved nothing; why.
    Inconclusive(String),
    /// Foreground work started; the request stays pending.
    Preempted,
}

/// Analyzes one file's media (§5.7).
pub type Analyzer = Arc<dyn Fn(Item) -> BoxFuture<'static, Analysis> + Send + Sync>;

#[derive(Clone, Debug)]
pub enum RepairTarget {
    Inspect,
    ArrPath(String),
    UnownedDelete,
}
pub type RepairAuthority =
    Arc<dyn Fn(ItemId, RepairTarget) -> BoxFuture<'static, Result<bool, Error>> + Send + Sync>;

pub type CachedCopy = Arc<dyn Fn(Item) -> BoxFuture<'static, Option<bool>> + Send + Sync>;

/// A durable local-repair queue hook. It must only admit/read back work, never
/// fetch provider data or publish while running inside the health worker.
pub type LocalRepair =
    Arc<dyn Fn(Item, HealthStatus) -> BoxFuture<'static, Result<bool, Error>> + Send + Sync>;

pub type ReadHold = Box<dyn std::any::Any + Send + Sync>;
pub struct Health {
    read_gate: Option<Box<dyn Fn(ItemId) -> Option<ReadHold> + Send + Sync>>,
    db: Db,
    client: Arc<Client>,
    arr: watch::Receiver<Vec<Arr>>,
    settings: watch::Receiver<Settings>,
    importing: Gate,
    streaming: Gate,
    in_use: Box<dyn Fn(ItemId) -> bool + Send + Sync>,
    analyzer: Option<Analyzer>,
    cached_copy: Option<CachedCopy>,
    local_repair: Option<LocalRepair>,
    repair_authority: Option<RepairAuthority>,
    http: reqwest::Client,
    arr_wait: Duration,
    counters: [AtomicU64; 4],
    inconclusive: Mutex<HashMap<ItemId, u32>>,
    wake: Notify,
    legacy_wake: Notify,
    sweep_gate: tokio::sync::Mutex<()>,
    publication_gate: tokio::sync::Mutex<()>,
    mutation_gate: Arc<tokio::sync::RwLock<()>>,
    sweep_cancel: watch::Sender<u64>,
    priority_cancel: watch::Sender<u64>,
    priority_streak: AtomicU64,
    maintenance_turn: AtomicU64,
}

/// Checks since start, by result.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Stats {
    /// Healthy or degraded.
    pub healthy: u64,
    pub unhealthy: u64,
    /// A provider failed, so the check proved nothing.
    pub inconclusive: u64,
    /// Replacements requested from an Arr.
    pub repaired: u64,
}

const HEALTHY: usize = 0;
const UNHEALTHY: usize = 1;
const INCONCLUSIVE: usize = 2;
const REPAIRED: usize = 3;

/// Who can replace a broken file.
enum Lookup {
    Found {
        arr: Arr,
        owner: Box<arr::Owner>,
        /// The download it came from, when known.
        job: Option<JobId>,
        job_name: String,
    },
    /// No Arr imported it; the reason why.
    Unowned(String),
    /// An Arr owns it but cannot be asked for a replacement; the reason why.
    Unrepairable(String),
    /// An Arr failed; `transient` when retrying later may work.
    Failed { message: String, transient: bool },
}

impl Health {
    /// `arr` carries the enabled Arr instances and `settings` the §10 choices; both follow
    /// settings edits.
    pub fn new(
        db: Db,
        client: Arc<Client>,
        arr: watch::Receiver<Vec<Arr>>,
        settings: watch::Receiver<Settings>,
    ) -> Result<Self, Error> {
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .redirect(reqwest::redirect::Policy::none())
            .build()?;
        Ok(Self {
            read_gate: None,
            db,
            client,
            arr,
            settings,
            importing: Box::new(|| false),
            streaming: Box::new(|| false),
            in_use: Box::new(|_| false),
            analyzer: None,
            cached_copy: None,
            local_repair: None,
            repair_authority: None,
            http,
            arr_wait: ARR_WAIT,
            counters: Default::default(),
            inconclusive: Mutex::default(),
            wake: Notify::new(),
            legacy_wake: Notify::new(),
            sweep_gate: tokio::sync::Mutex::new(()),
            publication_gate: tokio::sync::Mutex::new(()),
            mutation_gate: Arc::new(tokio::sync::RwLock::new(())),
            sweep_cancel: watch::channel(0).0,
            priority_cancel: watch::channel(0).0,
            priority_streak: AtomicU64::new(0),
            maintenance_turn: AtomicU64::new(0),
        })
    }

    /// Pauses health work while `importing` and lowers probe concurrency while
    /// `streaming`.
    #[must_use]
    pub fn with_gates(
        mut self,
        importing: impl Fn() -> bool + Send + Sync + 'static,
        streaming: impl Fn() -> bool + Send + Sync + 'static,
    ) -> Self {
        self.importing = Box::new(importing);
        self.streaming = Box::new(streaming);
        self
    }

    /// Holds back the repair of a file while `in_use` says it is open.
    #[must_use]
    pub fn with_in_use(mut self, in_use: impl Fn(ItemId) -> bool + Send + Sync + 'static) -> Self {
        self.in_use = Box::new(in_use);
        self
    }

    /// The production VFS grants this only after all readers close, and blocks opens
    /// until the returned guard drops. Standalone callers may keep the predicate only.
    #[must_use]
    pub fn with_read_gate(
        mut self,
        gate: impl Fn(ItemId) -> Option<ReadHold> + Send + Sync + 'static,
    ) -> Self {
        self.read_gate = Some(Box::new(gate));
        self
    }
    /// Some(true) means a complete verified final-media copy; None means storage
    /// uncertainty. Neither authorizes deleting the source. Some(false) permits repair.
    #[must_use]
    pub fn with_cached_copy(mut self, check: CachedCopy) -> Self {
        self.cached_copy = Some(check);
        self
    }

    /// Admit and observe durable local repair before starting source replacement.
    #[must_use]
    pub fn with_local_repair(mut self, repair: LocalRepair) -> Self {
        self.local_repair = Some(repair);
        self
    }

    /// Source publication takes this barrier exclusively; replacement/unowned
    /// deletion holds a reader, then checks newly published repairs again.
    #[must_use]
    pub fn mutation_gate(&self) -> Arc<tokio::sync::RwLock<()>> {
        Arc::clone(&self.mutation_gate)
    }

    #[must_use]
    pub fn with_repair_authority(mut self, authority: RepairAuthority) -> Self {
        self.repair_authority = Some(authority);
        self
    }
    /// Local read-only discovery cannot grant replacement or deletion authority.
    pub async fn repair_allowed(&self, item: ItemId, path: Option<String>) -> Result<bool, Error> {
        if self.db.legacy_repair_held(item).await? {
            return Ok(false);
        }
        match &self.repair_authority {
            Some(authority) => {
                authority(
                    item,
                    path.map_or(RepairTarget::Inspect, RepairTarget::ArrPath),
                )
                .await
            }
            None => Ok(true),
        }
    }

    /// How long an Arr gets to queue a replacement on its own after a blocklist.
    #[must_use]
    pub const fn with_arr_wait(mut self, wait: Duration) -> Self {
        self.arr_wait = wait;
        self
    }

    /// Runs pending media analyses with `analyzer` while nothing is importing or
    /// streaming.
    #[must_use]
    pub fn with_analyzer(mut self, analyzer: Analyzer) -> Self {
        self.analyzer = Some(analyzer);
        self
    }

    /// Whether media analysis is available.
    pub const fn analyzes(&self) -> bool {
        self.analyzer.is_some()
    }

    pub fn stats(&self) -> Stats {
        let get = |i: usize| self.counters[i].load(Ordering::Relaxed);
        Stats {
            healthy: get(HEALTHY),
            unhealthy: get(UNHEALTHY),
            inconclusive: get(INCONCLUSIVE),
            repaired: get(REPAIRED),
        }
    }

    fn count(&self, counter: usize) {
        self.counters[counter].fetch_add(1, Ordering::Relaxed);
    }

    /// Awaits Arr or provider `work` with the step's write permit released, so a database
    /// compaction need not wait for it, then takes the permit back for the writes after.
    async fn released<T>(&self, writing: &mut Writing, work: impl Future<Output = T>) -> T {
        *writing = None;
        let output = work.await;
        *writing = Some(self.db.write_permit().await);
        output
    }

    pub async fn run(&self) {
        tokio::time::sleep(STARTUP_DELAY).await;
        // Checked every turn, so a steady stream of due work cannot hold it off.
        let mut reconcile_at = tokio::time::Instant::now();
        loop {
            // Persisted holds also recover after a crash, when no handles survive.
            if let Ok(held) = self.db.reader_held_repairs().await {
                for item in held {
                    if let Err(error) = self.readers_closed(item).await {
                        tracing::warn!(reason=%error, "reader-held repair recovery deferred");
                    }
                }
            }
            if tokio::time::Instant::now() >= reconcile_at {
                self.forget_deleted().await;
                reconcile_at = tokio::time::Instant::now() + IDLE_POLL;
            }
            match self.step().await {
                Ok(true) => {}
                Ok(false) => {
                    self.housekeep().await;
                    let woken = self.wake.notified();
                    let _ = tokio::time::timeout(IDLE_POLL, woken).await;
                }
                Err(error) => {
                    tracing::warn!(reason = %error, "health work failed");
                    tokio::time::sleep(IDLE_POLL).await;
                }
            }
        }
    }

    /// Advance only a repair explicitly held for a reader. Re-check after notification
    /// because another handle may have opened in the meantime.
    pub async fn readers_closed(&self, item: ItemId) -> Result<(), Error> {
        if (self.in_use)(item) {
            return Ok(());
        }
        let _writing = self.db.write_permit().await;
        self.readers_closed_admitted(item).await
    }
    /// Internal callers already own write admission; acquiring it again can wait
    /// behind maintenance while that maintenance waits for the caller itself.
    async fn readers_closed_admitted(&self, item: ItemId) -> Result<(), Error> {
        if (self.in_use)(item) {
            return Ok(());
        }
        if self.db.wake_legacy_retirement(item).await? {
            self.legacy_wake.notify_one();
        }
        let mut wake = self.db.release_reader_repair(item).await?;
        let automatic = self.settings.borrow().auto_repair;
        if automatic
            && let Some(local) = &self.local_repair
            && let Some((current, status)) = self.db.health(item).await?
        {
            wake |= local(current, status).await?;
        }
        if wake {
            self.priority_cancel
                .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
            self.wake.notify_one();
        }
        Ok(())
    }

    /// Drops the inconclusive streaks of items deleted since they
    /// were recorded. Item ids are never reused, so a missing item is gone for good.
    async fn forget_deleted(&self) {
        let ids: Vec<ItemId> = self
            .inconclusive
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .keys()
            .copied()
            .collect();
        let mut gone = Vec::new();
        for id in ids {
            match self.db.item(id).await {
                Ok(Some(_)) => {}
                Ok(None) => gone.push(id),
                Err(error) => {
                    tracing::warn!(reason = %error, "could not check health bookkeeping against the library");
                    return;
                }
            }
        }
        if gone.is_empty() {
            return;
        }
        let mut inconclusive = self
            .inconclusive
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        for id in &gone {
            inconclusive.remove(id);
        }
    }

    async fn housekeep(&self) {
        let _writing = self.db.write_permit().await;
        let now = now_ms();
        if let Err(error) = self.db.prune_health_checks(now - RETENTION_MS).await {
            tracing::warn!(reason = %error, "could not prune health check results");
        }
        if let Err(error) = self.db.prune_regrab_intents(now - RETENTION_MS).await {
            tracing::warn!(reason=%error,"could not prune completed regrab intents");
        }
        if let Err(error) = self.db.prune_rejected_releases(now).await {
            tracing::warn!(reason = %error, "could not prune rejected releases");
        }
    }

    /// Does the most urgent due piece of health work; `false` when none is due or an
    /// import is running, which gets the connections first.
    pub async fn step(&self) -> Result<bool, Error> {
        if (self.importing)() {
            return Ok(false);
        }
        let mut writing = Some(self.db.write_permit().await);
        let settings = *self.settings.borrow();
        let primary = self
            .db
            .due_health_work(now_ms(), settings.enabled, 1)
            .await?
            .pop();
        let urgent = primary.as_ref().is_some_and(|(_, status)| {
            status.recheck != Recheck::None
                || matches!(status.state, HealthState::Repairing | HealthState::Hidden)
        });
        let maintenance =
            primary.is_none() || !urgent || self.priority_streak.load(Ordering::Relaxed) >= 4;
        let mut chosen = None;
        if maintenance {
            let turn = self.maintenance_turn.fetch_add(1, Ordering::Relaxed) % 3;
            for offset in 0..3 {
                match (turn + offset) % 3 {
                    0 => match self.released(&mut writing, self.sweep_step()).await {
                        Ok(true) => {
                            self.priority_streak.store(0, Ordering::Relaxed);
                            return Ok(true);
                        }
                        Ok(false) => {}
                        Err(error) => {
                            tracing::warn!(reason=%error,"content sweep could not advance; other health work continues");
                        }
                    },
                    1 => {
                        if !(self.streaming)()
                            && let Some(analyzer) = &self.analyzer
                            && let Some((item, requested)) = self.db.next_media_probe().await?
                        {
                            self.analyze(analyzer, item, requested, &mut writing)
                                .await?;
                            self.priority_streak.store(0, Ordering::Relaxed);
                            return Ok(true);
                        }
                    }
                    _ => {
                        chosen = self
                            .db
                            .due_routine_health(now_ms(), settings.enabled)
                            .await?;
                        if chosen.is_some() {
                            break;
                        }
                    }
                }
            }
            self.priority_streak.store(0, Ordering::Relaxed);
        }
        let Some((item, status)) = chosen.or(primary) else {
            return Ok(false);
        };
        if urgent && !maintenance {
            self.priority_streak.fetch_add(1, Ordering::Relaxed);
        }
        let work = async {
            match status.state {
                HealthState::Repairing => self.repair(&item, status, &settings, &mut writing).await,
                HealthState::Hidden => self.reconcile(&item, status, &mut writing).await,
                _ if !media::is_checkable(&item.name) => {
                    let ignored = HealthStatus {
                        state: HealthState::Ignored,
                        next_check: None,
                        ..status
                    };
                    Ok(self
                        .db
                        .set_health(item.id, &ignored, status.recheck)
                        .await?)
                }
                _ => self.check(&item, status, &settings, &mut writing).await,
            }
        };
        // One file's failure must not keep it first in line and starve the rest.
        let reason = match AssertUnwindSafe(work).catch_unwind().await {
            Ok(Ok(())) => return Ok(true),
            Ok(Err(error)) => {
                std::iter::successors(Some(&error as &dyn std::error::Error), |e| e.source())
                    .map(ToString::to_string)
                    .collect::<Vec<_>>()
                    .join(": ")
            }
            Err(panic) => panic
                .downcast_ref::<&str>()
                .map(|s| (*s).to_owned())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".to_owned()),
        };
        tracing::warn!(item = %item.id, path = %item.path, reason = %reason, "health work on a file failed; retrying in an hour");
        drop(writing);
        let _writing = self.db.write_permit().await;
        self.db
            .postpone_health(item.id, now_ms() + HOUR_MS, status.recheck_generation)
            .await?;
        Ok(true)
    }

    /// Due file work in priority order. Sample sweeps and media analysis receive
    /// separate bounded maintenance slots and are not listed here.
    pub async fn schedule(
        &self,
        limit: i64,
    ) -> Result<Vec<(Item, HealthStatus)>, infinidysk_db::Error> {
        let routine = self.settings.borrow().enabled;
        self.db.due_health_work(i64::MAX, routine, limit).await
    }

    /// Callers hold database write admission before entry. Scheduling a fresh
    /// check and recording its local repair outcome commit atomically, so loss of
    /// the response cannot replay a completion or expose an old replacement state.
    pub async fn complete_local_repair(
        &self,
        attempt: &infinidysk_db::attempts::Attempt,
        done: infinidysk_db::attempts::Completion<'_>,
    ) -> Result<bool, Error> {
        let _mutation = Arc::clone(&self.mutation_gate).write_owned().await;
        let _publication = self.publication_gate.lock().await;
        let changed = self.db.complete_repair_attempt(attempt, done).await?;
        if changed {
            self.priority_cancel
                .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
            self.wake.notify_one();
        }
        Ok(changed)
    }

    /// Asks for a check of `id` soon; an `action_needed` file starts over as unchecked.
    /// `false` if there is no such file.
    pub async fn request_check(&self, id: ItemId) -> Result<bool, Error> {
        let Some((_, mut status)) = self.db.health(id).await? else {
            return Ok(false);
        };
        if status.state == HealthState::ActionNeeded {
            status.state = HealthState::Unchecked;
            status.repair_attempts = 0;
            self.db.set_health(id, &status, Recheck::None).await?;
        }
        self.db.request_recheck(id, Recheck::User).await?;
        self.priority_cancel
            .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
        self.wake.notify_one();
        Ok(true)
    }

    /// Starts a repair of `id` now, whatever its checks found; the repair budget still
    /// applies. `false` if there is no such file.
    pub async fn request_repair(&self, id: ItemId) -> Result<bool, Error> {
        let Some((_, mut status)) = self.db.health(id).await? else {
            return Ok(false);
        };
        if !self.repair_allowed(id, None).await? {
            return Err(Error::Regrab(
                "source has no managed library mapping; read-only scan folders cannot authorize replacement",
            ));
        }
        if status.state != HealthState::Hidden {
            status.state = HealthState::Repairing;
            status.next_check = Some(now_ms());
            self.db.set_health(id, &status, Recheck::None).await?;
            self.priority_cancel
                .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
            self.wake.notify_one();
        }
        Ok(true)
    }

    /// Asks for a media analysis of `id` soon. `false` if there is no such file.
    pub async fn request_analysis(&self, id: ItemId) -> Result<bool, Error> {
        if self.db.health(id).await?.is_none() {
            return Ok(false);
        }
        self.db.request_media_probe(id, now_ms()).await?;
        self.wake.notify_one();
        Ok(true)
    }

    async fn analyze(
        &self,
        analyzer: &Analyzer,
        item: Item,
        requested: i64,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        let (id, path) = (item.id, item.path.clone());
        let analysis = self.released(writing, async {tokio::select! {result=analyzer(item)=>result,()=self.wake.notified()=>Analysis::Preempted}}).await;
        let (outcome, reason, info) = match analysis {
            Analysis::Preempted => {
                tracing::debug!(path = %path, "media analysis yielded to foreground work");
                return Ok(());
            }
            Analysis::Discovered(info) => (ProbeOutcome::Discovered, None, Some(info)),
            Analysis::NoStreams => (
                ProbeOutcome::NoStreams,
                Some("no audio or video stream".to_owned()),
                None,
            ),
            Analysis::Unsupported(reason) => (ProbeOutcome::Unsupported, Some(reason), None),
            Analysis::Inconclusive(reason) => {
                tracing::debug!(path = %path, reason = %reason, "media analysis inconclusive");
                (ProbeOutcome::Inconclusive, Some(reason), None)
            }
        };
        let attempt = ProbeAttempt {
            at: now_ms(),
            outcome,
            reason: reason.as_deref(),
            info: info.as_deref(),
        };
        Ok(self.db.finish_media_probe(id, requested, &attempt).await?)
    }

    /// Records segments playback found `missing` or `corrupt` (flat indices across parts)
    /// as the first confirmed miss of `id`, and checks it next.
    pub async fn report(
        &self,
        id: ItemId,
        missing: &[usize],
        corrupt: &[usize],
    ) -> Result<(), Error> {
        let _writing = self.db.write_permit().await;
        self.report_source(id, None, missing, corrupt)
            .await
            .map(|_| ())
    }
    async fn report_source(
        &self,
        id: ItemId,
        identity: Option<&[u8]>,
        missing: &[usize],
        corrupt: &[usize],
    ) -> Result<bool, Error> {
        let _publication = self.publication_gate.lock().await;
        let Some((item, mut status)) = self.db.health(id).await? else {
            return Ok(false);
        };
        if !self.mark_current(&item, identity, missing, corrupt).await? {
            return Ok(false);
        }
        if matches!(
            status.state,
            HealthState::Unchecked | HealthState::Healthy | HealthState::Degraded
        ) {
            status.state = HealthState::Suspect;
            status.suspect_since = Some(now_ms());
            self.db.set_health(id, &status, Recheck::None).await?;
        }
        if status.state == HealthState::Suspect {
            // A file suspect only for its container now has its first article miss.
            let probe = self.db.media_probe(id).await?;
            if probe.structural_hold {
                self.db
                    .set_structural(id, probe.defect.as_ref(), false)
                    .await?;
                status.suspect_since = Some(now_ms());
                self.db.set_health(id, &status, Recheck::None).await?;
            }
            self.db.request_recheck(id, Recheck::Playback).await?;
            self.priority_cancel
                .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
            self.wake.notify_one();
            if self.settings.borrow().auto_repair
                && let Some(local) = &self.local_repair
            {
                let current = self.db.health(id).await?;
                if let Some((current, status)) = current {
                    local(current, status).await?;
                }
            }
        }
        Ok(true)
    }

    async fn check(
        &self,
        item: &Item,
        status: HealthStatus,
        settings: &Settings,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        let Some((blob_id, raw, mut meta)) = self.meta_snapshot(item).await? else {
            tracing::warn!(item = %item.id, path = %item.path, reason = "its stored metadata is gone", "cannot check a file; retrying tomorrow");
            self.db
                .postpone_health(item.id, now_ms() + DAY_MS, status.recheck_generation)
                .await?;
            return Ok(());
        };
        let repair_generation = self.client.repair_generation();
        let opts = check::Options {
            percent: settings.sample_percent,
            depth: settings.sample_depth,
            tolerant: settings.degraded_tolerance,
            concurrency: if (self.streaming)() {
                CONCURRENCY_STREAMING
            } else {
                CONCURRENCY_IDLE
            },
            proven: false,
        };
        let client = check::Direct(&self.client);
        let (outcome, changed, head) = self
            .released(writing, check::run(&client, &mut meta, &item.name, &opts))
            .await;
        let found = if matches!(outcome, Outcome::Healthy | Outcome::Degraded(_)) {
            Some(
                self.released(writing, structure::walk(&client, &meta, &item.name, head))
                    .await,
            )
        } else {
            None
        };
        // Hold source publication out from the generation comparison through
        // verdict/receipt writes. A comparison alone leaves a publication race.
        let _source_publication = Arc::clone(&self.mutation_gate).read_owned().await;
        let _publication = self.publication_gate.lock().await;
        if self
            .db
            .health(item.id)
            .await?
            .is_none_or(|(_, current)| current.recheck_generation != status.recheck_generation)
        {
            return Ok(());
        }
        if self.client.repair_generation() != repair_generation {
            self.db
                .postpone_health(item.id, now_ms() + 5_000, status.recheck_generation)
                .await?;
            return Ok(());
        }
        let next_raw = if changed {
            Some(blob::encode_meta(&meta)?)
        } else {
            None
        };
        let current = if let Some(next_raw) = &next_raw {
            self.db
                .update_metadata_if_current(item.id, blob_id, &raw, next_raw)
                .await?
        } else {
            self.db.metadata_is_current(item.id, blob_id, &raw).await?
        };
        if !current {
            self.db
                .postpone_health(item.id, now_ms() + MINUTE_MS, status.recheck_generation)
                .await?;
            return Ok(());
        }
        let checked_repair = if matches!(&outcome, Outcome::Inconclusive(_)) {
            None
        } else {
            self.repair_identity(blob_id, &meta)?
        };
        let probe = self.db.media_probe(item.id).await?;
        let now = now_ms();
        let mut next = HealthStatus {
            last_check: Some(now),
            ..status
        };
        let streak = {
            let mut map = self
                .inconclusive
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            if matches!(outcome, Outcome::Inconclusive(_)) {
                let n = map.entry(item.id).or_default();
                *n += 1;
                *n
            } else {
                map.remove(&item.id);
                0
            }
        };
        match outcome {
            Outcome::Inconclusive(error) => {
                self.count(INCONCLUSIVE);
                if streak == INCONCLUSIVE_WARN {
                    tracing::warn!(path = %item.path, reason = %error, "health checks keep failing to reach providers; retrying every 30 minutes");
                } else {
                    tracing::debug!(path = %item.path, reason = %error, "health check inconclusive");
                }
                next.next_check = Some(
                    now + if self.db.health_is_urgent(item.id).await? {
                        5 * MINUTE_MS
                    } else {
                        INCONCLUSIVE_RECHECK_MS
                    },
                );
            }
            Outcome::Missing(message) => {
                self.count(UNHEALTHY);
                // The second miss counts only 15 minutes after the first; `None` predates
                // the record and is old enough.
                let confirm_at = status.suspect_since.unwrap_or(0) + SUSPECT_RECHECK_MS;
                let message = match status.state {
                    HealthState::ActionNeeded => {
                        self.record(item, HealthResult::Missing, Repair::None, &message)
                            .await?;
                        next.next_check = Some(now + self.action_needed_interval(item).await?);
                        None
                    }
                    HealthState::Suspect if !probe.structural_hold && now < confirm_at => {
                        tracing::debug!(path = %item.path, "miss confirmed too soon after the first; confirming again later");
                        next.next_check = Some(confirm_at);
                        None
                    }
                    HealthState::Suspect if !probe.structural_hold => {
                        next.state = HealthState::Repairing;
                        next.next_check = Some(now);
                        Some(format!("{message}; confirmed, repairing"))
                    }
                    _ => {
                        if probe.structural_hold {
                            self.db
                                .set_structural(item.id, probe.defect.as_ref(), false)
                                .await?;
                        }
                        tracing::warn!(path = %item.path, reason = %message, "file has missing articles; re-checking in 15 minutes");
                        next.state = HealthState::Suspect;
                        next.suspect_since = Some(now);
                        next.next_check = Some(now + SUSPECT_RECHECK_MS);
                        Some(format!("{message}; re-checking in 15 minutes"))
                    }
                };
                if let Some(message) = message {
                    self.record(item, HealthResult::Missing, Repair::None, &message)
                        .await?;
                }
            }
            Outcome::Healthy | Outcome::Degraded(_) => {
                let Some(found) = found else {
                    return Err(Error::Regrab("missing structural check result"));
                };
                let source = structure::source(&meta);
                let verdict =
                    structure::judge(probe.defect, &found, source, now, SUSPECT_RECHECK_MS);
                if self
                    .structural(item, status, &mut next, verdict, now)
                    .await?
                {
                    self.db.set_health(item.id, &next, status.recheck).await?;
                    return Ok(());
                }
                self.count(HEALTHY);
                let (state, result, message) = match outcome {
                    Outcome::Degraded(m) => (HealthState::Degraded, HealthResult::Degraded, m),
                    _ => (HealthState::Healthy, HealthResult::Healthy, String::new()),
                };
                next.state = state;
                next.repair_attempts = 0;
                next.next_check = Some(cadence(item.release_date.unwrap_or(item.created_at), now));
                self.record(item, result, Repair::None, &message).await?;
                if settings.media_probe
                    && self.analyzer.is_some()
                    && media::is_analyzable(&item.name)
                    && wants_analysis(&probe)
                {
                    self.db.request_media_probe(item.id, now).await?;
                }
            }
        }
        self.db.set_health(item.id, &next, status.recheck).await?;
        if next.state == HealthState::Suspect
            && settings.auto_repair
            && let Some(local) = &self.local_repair
            && let Some((current, status)) = self.db.health(item.id).await?
        {
            local(current, status).await?;
        }
        if let Some(revision) = checked_repair {
            self.db
                .record_health_repair_observation(
                    item.id,
                    &revision,
                    blob_id,
                    next_raw.as_deref().unwrap_or(&raw),
                    status.recheck_generation,
                )
                .await?;
        }
        Ok(())
    }

    /// Applies container evidence to a file whose articles are fine; `true` when the
    /// evidence decided `next`.
    async fn structural(
        &self,
        item: &Item,
        status: HealthStatus,
        next: &mut HealthStatus,
        verdict: Verdict,
        now: i64,
    ) -> Result<bool, Error> {
        let defect = match verdict {
            Verdict::Nothing => return Ok(false),
            Verdict::Clear => {
                self.db.set_structural(item.id, None, false).await?;
                return Ok(false);
            }
            Verdict::Keep(_) => {
                self.count(INCONCLUSIVE);
                next.next_check = Some(now + INCONCLUSIVE_RECHECK_MS);
                return Ok(true);
            }
            Verdict::First(d) | Verdict::Wait(d) | Verdict::Confirm(d) => d,
        };
        self.count(UNHEALTHY);
        let message = broken(&defect);
        let (hold, message) = match (status.state, &verdict) {
            (HealthState::ActionNeeded, _) => {
                next.next_check = Some(now + self.action_needed_interval(item).await?);
                (false, Some(message))
            }
            (_, Verdict::Confirm(_)) => {
                next.state = HealthState::Repairing;
                next.next_check = Some(now);
                (false, Some(format!("{message}; confirmed, repairing")))
            }
            _ => {
                next.state = HealthState::Suspect;
                next.next_check = Some(defect.seen_at + SUSPECT_RECHECK_MS);
                let first = matches!(verdict, Verdict::First(_));
                if first {
                    tracing::warn!(path = %item.path, reason = %message, "file has a broken container; re-checking in 15 minutes");
                }
                (
                    true,
                    first.then(|| format!("{message}; re-checking in 15 minutes")),
                )
            }
        };
        self.db.set_structural(item.id, Some(&defect), hold).await?;
        if let Some(message) = message {
            self.record(item, HealthResult::Damaged, Repair::None, &message)
                .await?;
        }
        Ok(true)
    }

    /// Daily, weekly once the last few results were identical.
    async fn action_needed_interval(&self, item: &Item) -> Result<i64, Error> {
        let recent = self.db.item_checks(item.id, ACTION_NEEDED_SETTLE).await?;
        let settled = i64::try_from(recent.len()) == Ok(ACTION_NEEDED_SETTLE)
            && recent
                .windows(2)
                .all(|w| (w[0].result, &w[0].message) == (w[1].result, &w[1].message));
        Ok(if settled { 7 * DAY_MS } else { DAY_MS })
    }

    fn repair_identity(&self, blob: BlobId, meta: &FileMeta) -> Result<Option<[u8; 32]>, Error> {
        let mut hash = Sha256::new();
        hash.update(b"health-repair-observation-v1");
        hash.update(blob.0.as_bytes());
        hash.update(meta.content_identity()?);
        let mut repaired = false;
        let runs = match meta {
            FileMeta::Nzb(file) => vec![&file.segments],
            FileMeta::Multipart(file) => file.parts.iter().map(|p| &p.segments).collect(),
        };
        for run in runs {
            for segment in &run.items {
                match self.client.repair_revision(&segment.id) {
                    RepairRevision::Base => hash.update([0]),
                    RepairRevision::Pending => return Ok(None),
                    RepairRevision::Repaired(revision) => {
                        repaired = true;
                        hash.update([1]);
                        hash.update(revision);
                    }
                }
            }
        }
        Ok(repaired.then(|| hash.finalize().into()))
    }

    /// Publication can outlive the process that queued it. Never delete the source
    /// while its repair is pending, or before checking repairs that cover its recorded
    /// holes. Current bytes still need a fresh check; this does not declare health.
    async fn observe_overlay(&self, item: &Item, status: HealthStatus) -> Result<bool, Error> {
        if self.client.repair_generation() == 0 {
            return Ok(false);
        }
        let Some((blob_id, raw, meta)) = self.meta_snapshot(item).await? else {
            return Ok(false);
        };
        let runs = match &meta {
            FileMeta::Nzb(file) => vec![&file.segments],
            FileMeta::Multipart(file) => file.parts.iter().map(|p| &p.segments).collect(),
        };
        let mut pending = false;
        let mut holes = 0usize;
        let mut covered = true;
        for run in runs {
            pending |= run
                .items
                .iter()
                .any(|s| self.client.repair_revision(&s.id) == RepairRevision::Pending);
            for index in run.missing.iter().chain(&run.corrupt) {
                holes += 1;
                covered &= run.items.get(*index as usize).is_some_and(|s| {
                    matches!(
                        self.client.repair_revision(&s.id),
                        RepairRevision::Repaired(_)
                    )
                });
            }
        }
        if !pending && (holes == 0 || !covered) {
            return Ok(false);
        }
        if !pending
            && let Some(revision) = self.repair_identity(blob_id, &meta)?
            && self.db.health_repair_observed(item.id, &revision).await?
        {
            // Current repaired bytes already had a conclusive check. A tombstone
            // cannot keep resetting a confirmed-missing source into readback.
            return Ok(false);
        }
        let _publication = self.publication_gate.lock().await;
        if !self.db.metadata_is_current(item.id, blob_id, &raw).await?
            || self.db.health(item.id).await?.is_none_or(|(_, current)| {
                current.recheck_generation != status.recheck_generation
                    || current.state != status.state
            })
        {
            return Ok(true);
        }
        let next = HealthStatus {
            state: if pending {
                status.state
            } else {
                HealthState::Unchecked
            },
            next_check: Some(now_ms() + if pending { 5_000 } else { 0 }),
            ..status
        };
        self.db.set_health(item.id, &next, status.recheck).await?;
        if !pending {
            self.db.request_recheck(item.id, Recheck::Playback).await?;
        }
        self.record(item, HealthResult::Inconclusive, Repair::None,
            if pending { "verified article repair is awaiting durable publication; replacement deferred" }
            else { "verified repairs cover recorded source damage; checking current bytes before replacement" }).await?;
        Ok(true)
    }

    /// One step of §7.1 for a `repairing` file.
    async fn repair(
        &self,
        item: &Item,
        status: HealthStatus,
        settings: &Settings,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        if self.observe_overlay(item, status).await? {
            return Ok(());
        }
        if !settings.auto_repair {
            return self.give_up(item, status, "automatic repair is off").await;
        }
        if let Some(local) = &self.local_repair
            && local(item.clone(), status).await?
        {
            self.db
                .postpone_health(item.id, now_ms() + 5_000, status.recheck_generation)
                .await?;
            return Ok(());
        }
        if self.db.legacy_repair_held(item.id).await? {
            return self
                .give_up(
                    item,
                    status,
                    "legacy repair evidence requires reconciliation before replacement",
                )
                .await;
        }
        let _source_mutation = Arc::clone(&self.mutation_gate).read_owned().await;
        if self.observe_overlay(item, status).await? {
            return Ok(());
        }
        if status.repair_attempts >= REPAIR_BUDGET {
            return self
                .give_up(
                    item,
                    status,
                    "repeated replacements brought no working copy",
                )
                .await;
        }
        let now = now_ms();
        if (self.in_use)(item.id) {
            tracing::debug!(path = %item.path, "file is open; repair waits");
            let next = HealthStatus {
                next_check: Some(now + SUSPECT_RECHECK_MS),
                ..status
            };
            self.db.hold_repair_for_reader(item.id).await?;
            self.db.set_health(item.id, &next, status.recheck).await?;
            // Covers last-close racing the persisted hold publication.
            self.readers_closed_admitted(item.id).await?;
            return Ok(());
        }
        if let Some(check) = &self.cached_copy
            && self.db.media_probe(item.id).await?.defect.is_none()
        {
            match check(item.clone()).await {
                Some(false) => {}
                available => {
                    let (state, message, delay) = if available == Some(true) {
                        (
                            HealthState::Degraded,
                            "source is damaged; complete verified native copy retained",
                            DAY_MS,
                        )
                    } else {
                        (
                            HealthState::Repairing,
                            "native copy verification is unavailable; destructive repair waits",
                            HOUR_MS,
                        )
                    };
                    let next = HealthStatus {
                        state,
                        next_check: Some(now + delay),
                        ..status
                    };
                    self.db.set_health(item.id, &next, status.recheck).await?;
                    self.record(
                        item,
                        if available == Some(true) {
                            HealthResult::Degraded
                        } else {
                            HealthResult::Inconclusive
                        },
                        Repair::None,
                        message,
                    )
                    .await?;
                    return Ok(());
                }
            }
        }
        let _read_hold = if let Some(gate) = &self.read_gate {
            if let Some(hold) = gate(item.id) {
                Some(hold)
            } else {
                self.db.hold_repair_for_reader(item.id).await?;
                let next = HealthStatus {
                    next_check: Some(now + SUSPECT_RECHECK_MS),
                    ..status
                };
                self.db.set_health(item.id, &next, status.recheck).await?;
                return Ok(());
            }
        } else {
            None
        };
        if self.db.item_remap_pending(item.id).await? {
            let reason = "its Arr is rescanning a converted library file";
            return self.retry(item, status, reason, true).await;
        }
        let existing = self.db.regrab_intent(item.id).await?;
        let (arr, owner, job_name) = if let Some(row) = &existing {
            let saved: regrab::Saved = serde_json::from_str(&row.payload)?;
            let configured = self
                .arr
                .borrow()
                .iter()
                .find(|a| a.id.to_string() == saved.arr_id)
                .cloned();
            let Some(arr) = configured else {
                return self
                    .give_up(
                        item,
                        status,
                        "Arr for the outstanding regrab is unavailable; review its intent",
                    )
                    .await;
            };
            if regrab::scope(&self.db, item, &arr).await? != row.scope {
                return self
                    .give_up(
                        item,
                        status,
                        "source or Arr changed during regrab; review the outstanding intent",
                    )
                    .await;
            }
            (arr, Box::new(saved.progress.owner), saved.job_name)
        } else {
            match self.released(writing, self.owner(item)).await? {
                Lookup::Found {
                    arr,
                    owner,
                    job,
                    job_name,
                } => {
                    let _ = job;
                    (arr, owner, job_name)
                }
                Lookup::Unowned(reason) => {
                    if let Some(authority) = &self.repair_authority
                        && !authority(item.id, RepairTarget::UnownedDelete).await?
                    {
                        return self.give_up(item,status,"additional scan roots require Arr authority; automatic deletion of unowned sources is disabled").await;
                    }
                    return match settings.unowned_action {
                        UnownedAction::Delete => {
                            tracing::warn!(path = %item.path, reason = %reason, "deleted a broken file no Arr can replace");
                            self.record(
                                item,
                                HealthResult::Missing,
                                Repair::Deleted,
                                &format!("{reason}; deleted"),
                            )
                            .await?;
                            Ok(self.db.delete_item(item.id).await?)
                        }
                        UnownedAction::ActionNeeded => self.give_up(item, status, &reason).await,
                    };
                }
                Lookup::Unrepairable(reason) => return self.give_up(item, status, &reason).await,
                Lookup::Failed { message, transient } => {
                    return self.retry(item, status, &message, transient).await;
                }
            }
        };
        if !self
            .repair_allowed(item.id, Some(owner.file_path().to_owned()))
            .await?
        {
            return self
                .give_up(
                    item,
                    status,
                    "replacement requires a current managed library path; scan roots confer no authority",
                )
                .await;
        }
        // Blocklisting makes the Arr search, so the shared search limit applies first.
        let (kind, ids) = owner.searches();
        if existing.is_none()
            && !owner.replaced()
            && !self.db.reserve_searches(arr.id, kind, ids, now).await?
        {
            tracing::info!(path = %item.path, arr = %arr.name, "search limit reached; repair waits");
            let next = HealthStatus {
                next_check: Some(now + SEARCH_WAIT_MS),
                ..status
            };
            self.db.set_health(item.id, &next, status.recheck).await?;
            return Ok(());
        }
        let outcome = self
            .released(
                writing,
                self.tracked_regrab(item, &arr, &owner, &job_name, existing),
            )
            .await?;
        match outcome {
            Ok(()) => {
                self.reject(item, &job_name).await?;
                self.count(REPAIRED);
                tracing::info!(path = %item.path, arr = %arr.name, "replacement requested; hiding the file");
                let message = format!("{} is searching for a replacement", arr.name);
                self.record(
                    item,
                    HealthResult::Missing,
                    Repair::ReplacementRequested,
                    &message,
                )
                .await?;
                let next = HealthStatus {
                    state: HealthState::Hidden,
                    next_check: Some(now + HOUR_MS),
                    repair_attempts: status.repair_attempts + 1,
                    hidden_until: Some(now + HIDDEN_WAIT_MS),
                    ..status
                };
                self.db.set_health(item.id, &next, status.recheck).await?;
                Ok(())
            }
            Err(arr::Error::Searching) => {
                let next = HealthStatus {
                    next_check: Some(now + MINUTE_MS),
                    ..status
                };
                self.db.set_health(item.id, &next, status.recheck).await?;
                Ok(())
            }
            Err(arr::Error::Uncertain(message)) => {
                let next = HealthStatus {
                    next_check: Some(now + MINUTE_MS),
                    ..status
                };
                self.record(item, HealthResult::Inconclusive, Repair::None, message)
                    .await?;
                self.db.set_health(item.id, &next, status.recheck).await?;
                Ok(())
            }
            Err(error) => {
                let message = format!("{}: {error}", arr.name);
                self.retry(item, status, &message, error.is_transient())
                    .await
            }
        }
    }

    /// After an Arr failure: a transient one retries in an hour for free, others spend
    /// budget and back off.
    async fn retry(
        &self,
        item: &Item,
        status: HealthStatus,
        message: &str,
        transient: bool,
    ) -> Result<(), Error> {
        let now = now_ms();
        let mut next = status;
        if transient {
            tracing::warn!(path = %item.path, reason = %message, "Arr unavailable for repair; retrying in an hour");
            next.next_check = Some(now + ARR_RETRY_MS);
        } else {
            next.repair_attempts += 1;
            if next.repair_attempts >= REPAIR_BUDGET {
                return self.give_up(item, next, message).await;
            }
            let backoff = (HOUR_MS << (next.repair_attempts - 1)).min(DAY_MS);
            tracing::warn!(path = %item.path, reason = %message, "repair request failed");
            self.record(item, HealthResult::Missing, Repair::None, message)
                .await?;
            next.next_check = Some(now + backoff);
        }
        self.db.set_health(item.id, &next, status.recheck).await?;
        Ok(())
    }

    async fn give_up(&self, item: &Item, status: HealthStatus, reason: &str) -> Result<(), Error> {
        tracing::warn!(path = %item.path, reason = %reason, "broken file needs attention");
        self.record(item, HealthResult::Missing, Repair::ActionNeeded, reason)
            .await?;
        let next = HealthStatus {
            state: HealthState::ActionNeeded,
            next_check: Some(now_ms() + DAY_MS),
            hidden_until: None,
            ..status
        };
        self.db.set_health(item.id, &next, status.recheck).await?;
        Ok(())
    }

    /// Hourly look at a `hidden` file: gone once the Arr has its replacement, back to
    /// repairing (or `action_needed`) when the wait runs out.
    async fn reconcile(
        &self,
        item: &Item,
        status: HealthStatus,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        if self.db.legacy_repair_held(item.id).await? {
            return self
                .give_up(
                    item,
                    status,
                    "legacy repair evidence requires reconciliation before source removal",
                )
                .await;
        }
        let now = now_ms();
        let mut next = HealthStatus {
            next_check: Some(now + HOUR_MS),
            ..status
        };
        if self.observe_regrab(item).await? {
            self.record(
                item,
                HealthResult::Missing,
                Repair::Replaced,
                "Arr imported the journaled replacement",
            )
            .await?;
            return Ok(self.db.delete_item(item.id).await?);
        }
        let lookup = self.released(writing, self.owner(item)).await?;
        match lookup {
            Lookup::Found { arr, owner, .. } if owner.replaced() => {
                tracing::info!(path = %item.path, arr = %arr.name, "replacement imported; deleting the broken file");
                let message = format!("{} imported a replacement", arr.name);
                self.record(item, HealthResult::Missing, Repair::Replaced, &message)
                    .await?;
                return Ok(self.db.delete_item(item.id).await?);
            }
            Lookup::Failed {
                transient: true, ..
            } => {}
            _ if status.hidden_until.is_some_and(|t| now >= t) => {
                if status.repair_attempts >= REPAIR_BUDGET {
                    return self
                        .give_up(
                            item,
                            status,
                            "no replacement arrived after repeated requests",
                        )
                        .await;
                }
                self.record(
                    item,
                    HealthResult::Missing,
                    Repair::None,
                    "no replacement arrived in time; asking again",
                )
                .await?;
                next.state = HealthState::Repairing;
                next.next_check = Some(now);
                next.hidden_until = None;
            }
            _ => {}
        }
        // Rechecks wait until the file is visible again.
        self.db.set_health(item.id, &next, Recheck::None).await?;
        Ok(())
    }

    /// Finds the Arr instance and media that imported `item`.
    async fn owner(&self, item: &Item) -> Result<Lookup, Error> {
        // `/content/<category>/<job>/<file…>`; the Arr saw it under `<category>/<job>/<file…>`.
        let Some(dropped) = item.path.strip_prefix("/content") else {
            return Ok(Lookup::Unowned("not part of a download".to_owned()));
        };
        // A video handed off as a STRM was imported under `<stem>.strm` or `<name>.strm`.
        let mut dropped = vec![dropped.to_owned()];
        if media::is_video(&item.name) {
            if let Some((stem, _)) = dropped[0].rsplit_once('.') {
                dropped.push(format!("{stem}.strm"));
            }
            dropped.push(format!("{}.strm", dropped[0]));
        }
        let job_dir: String = item.path.split_inclusive('/').take(4).collect();
        let job_dir = job_dir.trim_end_matches('/');
        let stamped = self.db.job_at(job_dir).await?;
        // Files carried over from another install may know their download only from an Arr.
        let recorded_jobs = match stamped {
            Some(_) => Vec::new(),
            None => self.db.owner_jobs(item.id).await?,
        };
        let recorded = self.db.owner_instances(item.id).await?;
        let (mut instances, unrepairable): (Vec<Arr>, Vec<Arr>) = self
            .arr
            .borrow()
            .iter()
            .filter(|a| a.profile.repairs() || recorded.contains(&a.id))
            .cloned()
            .partition(|a| a.profile.repairs());
        // A recorded owner that cannot repair still owns it, so it is never unowned.
        let unrepairable = unrepairable.first().map(|a| {
            Lookup::Unrepairable(format!(
                "{} owns it but cannot be asked for a replacement",
                a.name
            ))
        });
        if instances.is_empty() {
            return Ok(unrepairable.unwrap_or_else(|| {
                Lookup::Unowned("no Arr instance is configured to replace it".to_owned())
            }));
        }
        let dir_name = job_dir.rsplit('/').next().unwrap_or_default();
        // Instances whose webhook told us they own it go first.
        instances.sort_by_key(|a| !recorded.contains(&a.id));
        let (mut failures, mut transient) = (Vec::new(), true);
        for instance in instances {
            let job = stamped.clone().or_else(|| {
                let own = recorded_jobs.iter().find(|(i, _)| *i == instance.id);
                own.or_else(|| recorded_jobs.first())
                    .map(|&(_, job)| (job, dir_name.to_owned()))
            });
            let recorded_file = self.db.owner_file(instance.id, item.id).await?;
            let found = match &job {
                Some((job, _)) => {
                    let job_id = job.to_string();
                    arr::owner(&self.http, &instance, &job_id, &dropped, recorded_file).await
                }
                None => Ok(None),
            };
            // Without the download in its history, the file id we recorded names the media.
            let found = match (found, recorded_file) {
                (Ok(None), Some(file)) => arr::owner_of_file(&self.http, &instance, file).await,
                (found, _) => found,
            };
            match found {
                Ok(Some(owner)) => {
                    let (job, job_name) = job.unzip();
                    return Ok(Lookup::Found {
                        arr: instance,
                        owner: Box::new(owner),
                        job,
                        job_name: job_name.unwrap_or_else(|| dir_name.to_owned()),
                    });
                }
                Ok(None) => {}
                Err(error) => {
                    transient &= error.is_transient();
                    failures.push(format!("{}: {error}", instance.name));
                }
            }
        }
        if !failures.is_empty() {
            return Ok(Lookup::Failed {
                message: failures.join("; "),
                transient,
            });
        }
        if let Some(unrepairable) = unrepairable {
            return Ok(unrepairable);
        }
        if stamped.is_none() && recorded_jobs.is_empty() {
            // The owner backfill may still adopt it.
            let backfilled = self.db.config().await?;
            if !backfilled.iter().any(|(k, _)| k == BACKFILLED_KEY) {
                return Ok(Lookup::Failed {
                    message: "waiting for the Arrs to report which files they imported".to_owned(),
                    transient: true,
                });
            }
            return Ok(Lookup::Unowned(
                "the download it came from is unknown".to_owned(),
            ));
        }
        Ok(Lookup::Unowned(
            "no Arr instance imported this file".to_owned(),
        ))
    }

    /// Remembers the release so the queue refuses it if the Arr grabs it again.
    async fn reject(&self, item: &Item, job_name: &str) -> Result<(), Error> {
        let ids: Vec<String> = match self.meta(item).await? {
            Some((_, FileMeta::Nzb(f))) => f
                .segments
                .items
                .into_iter()
                .take(REJECT_IDS)
                .map(|s| s.id)
                .collect(),
            Some((_, FileMeta::Multipart(f))) => {
                // Each volume's first article, which the queue compares.
                let firsts = f.parts.iter().filter_map(|p| p.segments.items.first());
                let rest = f.parts.iter().flat_map(|p| &p.segments.items);
                firsts
                    .chain(rest)
                    .take(REJECT_IDS)
                    .map(|s| s.id.clone())
                    .collect()
            }
            None => Vec::new(),
        };
        let ids: Vec<&str> = ids
            .iter()
            .map(String::as_str)
            .filter(|id| !id.is_empty())
            .collect();
        self.db
            .reject_release(job_name, &ids, now_ms() + REJECT_FOR_MS)
            .await?;
        Ok(())
    }

    async fn meta_snapshot(
        &self,
        item: &Item,
    ) -> Result<Option<(BlobId, Vec<u8>, FileMeta)>, Error> {
        let Some(id) = item.meta_blob else {
            return Ok(None);
        };
        let Some(bytes) = self.db.blob(id).await? else {
            return Ok(None);
        };
        let meta = blob::decode_meta(&bytes)?;
        Ok(Some((id, bytes, meta)))
    }
    async fn meta(&self, item: &Item) -> Result<Option<(BlobId, FileMeta)>, Error> {
        Ok(self
            .meta_snapshot(item)
            .await?
            .map(|(id, _, meta)| (id, meta)))
    }
    /// Merge faults without overwriting another reader's additions or a newer source.
    async fn mark_current(
        &self,
        item: &Item,
        identity: Option<&[u8]>,
        missing: &[usize],
        corrupt: &[usize],
    ) -> Result<bool, Error> {
        for _ in 0..4 {
            let Some((id, raw, mut meta)) = self.meta_snapshot(item).await? else {
                return Ok(false);
            };
            if let Some(identity) = identity
                && meta.content_identity()? != identity
            {
                return Ok(false);
            }
            if !check::mark(&mut meta, missing, corrupt) {
                if self.db.metadata_is_current(item.id, id, &raw).await? {
                    return Ok(true);
                }
            } else if self
                .db
                .update_metadata_if_current(item.id, id, &raw, &blob::encode_meta(&meta)?)
                .await?
            {
                return Ok(true);
            }
        }
        Err(Error::Regrab(
            "metadata changed repeatedly; retry the report",
        ))
    }

    async fn record(
        &self,
        item: &Item,
        result: HealthResult,
        repair: Repair,
        message: &str,
    ) -> Result<(), Error> {
        let check = HealthCheck {
            created_at: now_ms(),
            item_id: item.id,
            path: item.path.clone(),
            result,
            repair,
            message: (!message.is_empty()).then(|| message.to_owned()),
        };
        Ok(self.db.record_health_check(&check).await?)
    }
}

/// Whether a good file should be analyzed: metadata is missing and no attempt is pending
/// or found a lasting answer.
// ponytail: analysis runs once per file; re-analysis is a user action.
const fn wants_analysis(probe: &MediaProbe) -> bool {
    probe.info.is_none()
        && probe.requested_at.is_none()
        && !matches!(
            probe.outcome,
            Some(ProbeOutcome::NoStreams | ProbeOutcome::Unsupported)
        )
}

/// When a good file is next checked: after as long again as it has been posted (Unix ms),
/// at least an hour and at most 90 days, ±10 %.
fn cadence(posted: i64, now: i64) -> i64 {
    let age = (now - posted).clamp(HOUR_MS, CADENCE_CAP_MS);
    let jitter = check::random_below((age / 5).cast_unsigned()).cast_signed() - age / 10;
    now + age + jitter
}

fn broken(defect: &Defect) -> String {
    format!(
        "the container is broken: the box at byte {} claims {} bytes",
        defect.offset, defect.size
    )
}

/// Why an import's check of a file cannot count as its first health check.
#[derive(Debug)]
pub enum Unverified {
    /// Confirmed missing or corrupt articles; what was found.
    Damaged(String),
    /// A provider failed, so the check proved nothing.
    Inconclusive(infinidysk_nntp::Error),
}

/// The first health check of file `name`, run by its import right after every article
/// answered STAT (a 100 % sample): the routine check's BODY reads and container walk,
/// through the import's `articles`. `posted` is when it was posted (Unix ms), and
/// `analyze` asks for a media analysis of a good file. `None` for files health never
/// checks.
pub async fn first_check(
    articles: &dyn Articles,
    meta: &FileMeta,
    name: &str,
    posted: Option<i64>,
    analyze: bool,
) -> Result<Option<FirstCheck>, Unverified> {
    if !media::is_checkable(name) {
        return Ok(None);
    }
    let opts = check::Options {
        percent: 100,
        depth: media::SampleDepth::Complete,
        tolerant: false,
        concurrency: CONCURRENCY_IDLE,
        proven: true,
    };
    let (outcome, _, head) = check::run(articles, &mut meta.clone(), name, &opts).await;
    match outcome {
        Outcome::Healthy => {}
        Outcome::Degraded(message) | Outcome::Missing(message) => {
            return Err(Unverified::Damaged(message));
        }
        Outcome::Inconclusive(error) => return Err(Unverified::Inconclusive(error)),
    }
    let found = structure::walk(articles, meta, name, head).await;
    let now = now_ms();
    let verdict = structure::judge(
        None,
        &found,
        structure::source(meta),
        now,
        SUSPECT_RECHECK_MS,
    );
    let mut status = HealthStatus {
        last_check: Some(now),
        ..HealthStatus::default()
    };
    Ok(Some(if let Verdict::First(defect) = verdict {
        let message = broken(&defect);
        tracing::warn!(file = %name, reason = %message, "imported file has a broken container; re-checking in 15 minutes");
        status.state = HealthState::Suspect;
        status.next_check = Some(defect.seen_at + SUSPECT_RECHECK_MS);
        FirstCheck {
            status,
            result: HealthResult::Damaged,
            message: Some(format!("{message}; re-checking in 15 minutes")),
            defect: Some(defect),
            analyze: false,
        }
    } else {
        status.state = HealthState::Healthy;
        status.next_check = Some(cadence(posted.unwrap_or(now), now));
        FirstCheck {
            status,
            result: HealthResult::Healthy,
            message: None,
            defect: None,
            analyze: analyze && media::is_analyzable(name),
        }
    }))
}

#[cfg(test)]
mod tests {
    use infinidysk_core::ItemKind;

    use super::*;

    // Compile-time guard: borrowed iterators in `stream::iter` once made this future `!Send`.
    #[allow(dead_code, reason = "only needs to type-check")]
    fn run_is_spawnable(health: Arc<Health>) {
        tokio::spawn(async move { health.run().await });
    }

    #[tokio::test]
    async fn repair_publication_waits_until_the_health_verdict_and_receipt_are_durable() {
        use bytes::Bytes;
        use infinidysk_core::{ItemKind, NzbFile, Segment, Segments};
        use infinidysk_native_cache::repair::{Geometry, Patch, Proof, Store};
        let path = std::env::temp_dir().join(format!(
            "infinidysk-health-publication-{}",
            uuid::Uuid::new_v4()
        ));
        std::fs::create_dir(&path).expect("directory");
        let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
            .await
            .expect("db");
        let root = db
            .item(ItemId::CONTENT)
            .await
            .expect("root")
            .expect("content");
        let mut item = Item::child(&root, "fixture.mkv", ItemKind::NzbFile).expect("file");
        let blob = BlobId::new();
        item.meta_blob = Some(blob);
        item.size = Some(8);
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: "race@fixture".into(),
                    size: 8,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                ..Segments::default()
            },
            proof: None,
        });
        db.insert(
            std::slice::from_ref(&item),
            &[(blob, &blob::encode_meta(&meta).expect("encode"))],
        )
        .await
        .expect("insert");
        let patch = |data| Patch {
            message_id: "race@fixture".into(),
            geometry: Geometry {
                offset: 0,
                file_size: 8,
                part: None,
                total: None,
            },
            proof: Proof {
                set: [1; 16],
                file: [2; 16],
                file_md5: [3; 16],
                file_length: 8,
            },
            data: Bytes::from_static(data),
        };
        let store = Store::open(&path.join("repair")).await.expect("store");
        store
            .publish(vec![patch(b"restored")])
            .await
            .expect("old repair");
        let client = Arc::new(Client::new([]));
        assert!(client.set_repairs(store.clone()));
        let settings = Settings {
            media_probe: false,
            ..Settings::default()
        };
        let h = Arc::new(
            Health::new(
                db.clone(),
                client,
                watch::channel(vec![]).1,
                watch::channel(settings).1,
            )
            .expect("health"),
        );
        assert!(store.set_mutation_gate(h.mutation_gate()));
        let old_identity = h
            .repair_identity(blob, &meta)
            .expect("identity")
            .expect("repair");
        let (_, status) = db.health(item.id).await.expect("health").expect("file");
        let held = h.publication_gate.lock().await;
        let check = {
            let h = h.clone();
            let item = item.clone();
            tokio::spawn(async move {
                let mut writing = Some(h.db.write_permit().await);
                h.check(&item, status, &settings, &mut writing).await
            })
        };
        tokio::time::timeout(Duration::from_secs(5), async {
            while h.mutation_gate.try_write().is_ok() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("check holds the source publication guard before metadata writes");
        let publish = {
            let store = store.clone();
            let patch = patch(b"newbytes");
            tokio::spawn(async move { store.publish(vec![patch]).await })
        };
        tokio::time::timeout(Duration::from_secs(5), async {
            while h.mutation_gate.try_read().is_ok() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("publication queued");
        assert!(
            !store.is_pending(),
            "publication cannot activate between generation comparison and verdict persistence"
        );
        drop(held);
        check.await.expect("check task").expect("check");
        publish.await.expect("publication task").expect("publish");
        assert!(
            db.health_repair_observed(item.id, &old_identity)
                .await
                .expect("old verdict receipt")
        );
        let new_identity = h
            .repair_identity(blob, &meta)
            .expect("identity")
            .expect("new repair");
        assert_ne!(old_identity, new_identity);
        assert!(
            !db.health_repair_observed(item.id, &new_identity)
                .await
                .expect("new revision is unobserved")
        );
        drop(h);
        drop(store);
        drop(db);
        std::fs::remove_dir_all(path).expect("cleanup");
    }

    #[tokio::test]
    async fn deleted_items_leave_no_health_bookkeeping() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
        let kept = Item::child(&content, "kept.mkv", ItemKind::NzbFile).unwrap();
        let gone = Item::child(&content, "gone.mkv", ItemKind::NzbFile).unwrap();
        db.insert(&[kept.clone(), gone.clone()], &[]).await.unwrap();
        db.delete_item(gone.id).await.unwrap();
        let (_arr, arr) = watch::channel(Vec::new());
        let (_settings, settings) = watch::channel(Settings::default());
        let health = Health::new(db, Arc::new(Client::new([])), arr, settings).unwrap();
        for id in [kept.id, gone.id] {
            health.inconclusive.lock().unwrap().insert(id, 1);
        }

        health.forget_deleted().await;

        let inconclusive: Vec<ItemId> = health
            .inconclusive
            .lock()
            .unwrap()
            .keys()
            .copied()
            .collect();
        assert_eq!(inconclusive, [kept.id]);
    }

    #[test]
    fn cadence_follows_age_within_jitter() {
        let now = 400 * DAY_MS;
        let next = cadence(now - 10 * DAY_MS, now) - now;
        assert!((9 * DAY_MS..=11 * DAY_MS).contains(&next));
        assert!(cadence(0, now) - now <= CADENCE_CAP_MS * 11 / 10);
        assert!(cadence(now, now) - now >= HOUR_MS * 9 / 10);
    }
}
