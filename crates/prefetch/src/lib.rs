//! One bounded warming lane. Durable progress is a hint: every resumed block is verified
//! again. Only strict final-media bytes can enter the shared native cache.
mod lead;
mod watched;
use sha2::{Digest as _, Sha256};
use std::fmt::Write as _;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

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
const MAX_RANGES: usize = 64;
const RETRY_MS: i64 = 5 * 60 * 1000;
const EXPIRY_MS: i64 = 7 * 24 * 60 * 60 * 1000;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PrefetchSettings)]
pub struct Settings {
    pub enabled: bool,
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

pub struct Queue {
    db: Db,
    vfs: Arc<Vfs>,
    ledger: Mutex<Ledger>,
    /// A step holds this across its I/O. Controls use the ledger only and stay responsive.
    step: Mutex<()>,
    changed: Notify,
    wire: Arc<infinidysk_nntp::wire::Budget>,
    watched: Mutex<watched::Tracker>,
    lead: Mutex<lead::Lead>,
    uncertain: AtomicBool,
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
        Ok(Arc::new(Self {
            db,
            vfs,
            ledger: Mutex::new(ledger),
            step: Mutex::new(()),
            changed: Notify::new(),
            wire,
            watched: Mutex::default(),
            lead: Mutex::default(),
            uncertain: AtomicBool::new(false),
        }))
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
        let raw = serde_json::to_string(&next)?;
        let _writing = self.db.write_permit().await;
        self.uncertain.store(true, Ordering::Release);
        self.db.save_prefetch(&raw, &events).await?;
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
        Status {
            paused: ledger.paused,
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
        let job = self
            .admit(
                request.item,
                request.ranges,
                request.priority,
                request.intent,
                Some((request.scope, request.revision, Family::Plex)),
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
        ensure!(
            self.vfs.native_cache().is_some(),
            "native cache must be available before warming"
        );
        let mut current = self.lock_ledger().await?;
        if let Some(existing) = current.jobs.iter().find(|j| {
            j.item == key.item()
                && j.revision == key.revision()
                && j.intent == intent
                && j.ranges == ranges
                && (automatic.is_some()
                    || matches!(j.state, State::Queued | State::Paused | State::Complete))
        }) {
            return Ok(existing.clone());
        }
        let mut next = current.clone();
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
        let now = now_ms();
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
        drop(current);
        Ok(job)
    }
    pub async fn control(&self, id: Uuid, operation: Operation) -> anyhow::Result<()> {
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
    fn foreground(&self) -> bool {
        self.vfs.foreground_pending()
            || self
                .vfs
                .sessions()
                .open()
                .iter()
                .any(|s| s.waiting().is_some_and(|d| d >= Duration::from_millis(25)))
    }
    /// One block at most. The caller owns process cancellation and current settings.
    pub async fn tick(&self, settings: &Settings) -> anyhow::Result<bool> {
        let Ok(_step) = self.step.try_lock() else {
            return Ok(false);
        };
        self.wire
            .configure(settings.enabled, settings.daily_wire_bytes);
        if !settings.enabled || self.foreground() {
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
            let job = next
                .jobs
                .iter()
                .filter(|j| j.state == State::Queued && j.next_attempt <= now)
                .min_by_key(|j| {
                    let full =
                        j.ranges.len() == 1 && j.ranges[0].start == 0 && j.ranges[0].end == j.size;
                    (
                        !full || !protected.contains(&ItemId(j.item)),
                        std::cmp::Reverse(j.priority),
                        j.updated_at,
                        j.created_at,
                        j.id,
                    )
                })
                .cloned();
            if dirty {
                self.save(&mut current, next).await?;
            }
            drop(current);
            job
        };
        let Some(job) = selected else {
            return Ok(false);
        };
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
        let block = job.offset / BLOCK;
        if let Some(bytes) = cache.get(&lease, block).await {
            self.checkpoint(&job, bytes.len() as u64, false).await?;
            return Ok(true);
        }
        let length = BLOCK.min(job.size - job.offset);
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
            if current.charged.saturating_add(length) > settings.daily_fill_bytes {
                return Ok(false);
            }
            let mut next = current.clone();
            next.charged = next.charged.saturating_add(length);
            self.save(&mut current, next).await?;
            drop(current);
        }
        let mut reader = self.vfs.open_warming(&source).await?;
        reader.set_end(job.offset + length);
        let mut bytes = vec![0; usize::try_from(length)?];
        let transfer = async {
            reader.seek(std::io::SeekFrom::Start(job.offset)).await?;
            reader.read_exact(&mut bytes).await?;
            Ok::<_, std::io::Error>(())
        };
        let interrupted = async {
            loop {
                tokio::time::sleep(Duration::from_millis(50)).await;
                if self.foreground() {
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
        drop(reader);
        let Some(transferred) = transferred else {
            return Ok(false);
        };
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
        // Serialize publication against pause/cancel. No job can report completed bytes
        // before the complete block is safely published by the cache.
        let ledger = self.ledger.lock().await;
        if ledger.paused
            || !ledger
                .jobs
                .iter()
                .any(|j| j.id == job.id && j.state == State::Queued)
        {
            return Ok(false);
        }
        let published = cache
            .put_reserved(&lease, block, Bytes::from(bytes), permit)
            .await;
        drop(ledger);
        if matches!(published, Ok(true)) {
            self.checkpoint(&job, length, true).await?;
            Ok(true)
        } else {
            let mut current = self.lock_ledger().await?;
            let mut next = current.clone();
            if let Some(j) = next
                .jobs
                .iter_mut()
                .find(|j| j.id == job.id && j.state == State::Queued)
            {
                j.next_attempt = now_ms() + RETRY_MS;
                j.updated_at = now_ms();
                j.reason = Some(
                    "Cache publication deferred; check storage capacity and availability"
                        .to_owned(),
                );
            }
            self.save(&mut current, next).await?;
            drop(current);
            Ok(false)
        }
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
