use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock, PoisonError, RwLock};
use std::time::{Duration, Instant};

use infinidysk_core::trace::{self, Probe};
use tokio::sync::OwnedRwLockWriteGuard;
use tokio_util::sync::CancellationToken;

use crate::article::Body;
use crate::breaker::{Breaker, Outcome};
use crate::connection::validate_message_id;
use crate::missing::Missing;
use crate::pool::{Command, Failure, Load, Pool, Tag, join_all};
use crate::stats::{self, ProviderStats, Rescue, Stats, Workload};
use crate::{Error, Progress};

/// Requests in one lane may pipeline together, but never behind another lane's
/// unfinished reply. Import jobs use distinct lanes so a stalled job cannot capture
/// a different job's otherwise independent connection work.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Lane(pub(crate) u64);
impl Default for Lane {
    fn default() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(1);
        loop {
            let id = NEXT.fetch_add(1, Ordering::Relaxed);
            if id != 0 {
                return Self(id);
            }
        }
    }
}

/// Sees every body a request returns, with the message-id, who asked, and whether the
/// caller had already left (the reply was drained to keep the connection in sync).
pub type BodySink = Box<dyn Fn(&str, Workload, &Body, bool, RepairRevision) + Send + Sync>;

/// Captured before admission. A late original response must retain Base even if
/// publication has since installed a repaired article with the same message ID.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum RepairRevision {
    #[default]
    Base,
    Pending,
    Repaired([u8; 32]),
}
/// A durable, verified article source below every consumer of NNTP bodies. Missing
/// or uncertain repaired payloads are errors, never permission to use base bytes.
pub trait ArticleRepairs: Send + Sync {
    fn revision(&self, id: &str) -> RepairRevision;
    fn generation(&self) -> u64;
    fn expectation(&self, id: &str, revision: [u8; 32]) -> Result<crate::BodyExpectation, Error>;
    /// Immutable repaired-payload checksum, retained after intentional eviction.
    fn expected_hash(&self, id: &str, revision: [u8; 32]) -> Result<[u8; 32], Error>;
    /// `NotFound` means known payload absence. I/O uncertainty is `Unavailable`.
    fn body<'a>(&'a self, id: &'a str, revision: [u8; 32]) -> crate::ArticleFuture<'a, Body>;
}

/// One provider's pool and its place in the cascade.
pub struct Provider {
    pub pool: Pool,
    /// Stable across settings edits, so counters survive a [`Client::replace`].
    pub key: String,
    pub name: String,
    /// Lower levels are asked first; providers sharing a level form one pool and the
    /// next level is asked only after this one missed or failed.
    pub priority: u32,
    /// Providers on one backbone store the same articles, so one miss covers all.
    pub backbone: Option<String>,
}

/// Fetches bodies across providers by priority level, least busy first within a level,
/// failing over on misses, corrupt articles and provider failures.
// ponytail: providers are walked one at a time; add parallel STAT probing after the
// first miss when setups with 3+ backbones need it.
pub struct Client {
    /// Requests work on a snapshot, so a swap never cuts off transfers in flight; the old
    /// pools close once their last request finishes.
    providers: RwLock<Arc<[Entry]>>,
    /// Every provider ever configured; the index is its id.
    known: Mutex<Vec<Known>>,
    missing: Missing,
    sink: Arc<OnceLock<BodySink>>,
    repairs: OnceLock<Arc<dyn ArticleRepairs>>,
    /// Rare (a miss followed by a hit), so a lock is fine.
    rescues: Mutex<HashMap<(u16, u16, trace::Outcome), u64>>,
    /// Background requests pass this before being counted; a [`Lease`] holds it shut.
    gate: Arc<tokio::sync::RwLock<()>>,
    /// The current [`Lease`]'s token; playback cancels it instead of waiting.
    preempt: Arc<Mutex<Option<CancellationToken>>>,
    /// Requests admitted and not yet finished.
    admitted: Arc<AtomicUsize>,
    foreground: Arc<AtomicUsize>,
    warm_wire: OnceLock<Arc<crate::wire::Budget>>,
    warm_lane: Lane,
}

fn warm_target(floor: usize, held: usize, total: usize, free: usize, pressure: bool) -> usize {
    let floor = floor.clamp(1, 8);
    if pressure {
        return floor;
    }
    held.saturating_add(free)
        .saturating_sub(total.div_ceil(4))
        .clamp(floor, 24)
}

/// Exclusive use of the providers for a diagnostic: background requests wait at
/// admission until it drops, and playback cancels [`token`](Self::token).
pub struct Lease {
    _gate: OwnedRwLockWriteGuard<()>,
    token: CancellationToken,
    preempt: Arc<Mutex<Option<CancellationToken>>>,
}

impl Lease {
    /// Cancelled when playback needs the providers.
    #[must_use]
    pub const fn token(&self) -> &CancellationToken {
        &self.token
    }
}

impl Drop for Lease {
    fn drop(&mut self) {
        *self.preempt.lock().unwrap_or_else(PoisonError::into_inner) = None;
    }
}

struct Known {
    key: String,
    name: String,
    stats: Arc<Stats>,
}

struct Entry {
    provider: Provider,
    breaker: Breaker,
    id: u16,
    stats: Arc<Stats>,
}

impl Entry {
    /// Request order: lower first.
    fn preference(&self) -> (u32, bool, bool, usize) {
        // A due probe goes first on its level, or a peer serving every request
        // keeps it from ever being sent.
        let due = self.breaker.probe_due();
        (
            self.provider.priority,
            self.breaker.is_open() && !due,
            !due,
            self.provider.pool.busy_permille(),
        )
    }
}

impl Client {
    /// Without providers every request fails with [`Error::Unavailable`].
    pub fn new(providers: impl IntoIterator<Item = Provider>) -> Self {
        let client = Self {
            providers: RwLock::new(Arc::new([])),
            known: Mutex::default(),
            missing: Missing::default(),
            sink: Arc::default(),
            repairs: OnceLock::new(),
            rescues: Mutex::default(),
            gate: Arc::default(),
            preempt: Arc::default(),
            admitted: Arc::default(),
            foreground: Arc::default(),
            warm_wire: OnceLock::new(),
            warm_lane: Lane::default(),
        };
        client.replace(providers);
        client
    }

    /// Switches every new request to `providers`, with fresh circuit breakers.
    pub fn replace(&self, providers: impl IntoIterator<Item = Provider>) {
        let next = {
            let mut known = self.known.lock().unwrap_or_else(PoisonError::into_inner);
            providers
                .into_iter()
                .map(|mut provider| {
                    let index = known
                        .iter()
                        .position(|k| k.key == provider.key)
                        .unwrap_or_else(|| {
                            known.push(Known {
                                key: provider.key.clone(),
                                name: String::new(),
                                stats: Arc::default(),
                            });
                            known.len() - 1
                        });
                    known[index].name.clone_from(&provider.name);
                    provider.pool.set_stats(Arc::clone(&known[index].stats));
                    Entry {
                        stats: Arc::clone(&known[index].stats),
                        id: u16::try_from(index).unwrap_or(u16::MAX),
                        provider,
                        breaker: Breaker::new(),
                    }
                })
                .collect()
        };
        *self
            .providers
            .write()
            .unwrap_or_else(PoisonError::into_inner) = next;
    }

    /// Sets the [`BodySink`]; only the first call takes effect.
    pub fn warming_budget(&self) -> Option<&Arc<crate::wire::Budget>> {
        self.warm_wire.get()
    }
    pub fn set_warming_budget(&self, budget: Arc<crate::wire::Budget>) -> bool {
        self.warm_wire.set(budget).is_ok()
    }

    pub fn set_sink(&self, sink: BodySink) {
        let _ = self.sink.set(sink);
    }

    /// Attaches the app-wide durable repair ledger once, before serving requests.
    pub fn set_repairs(&self, source: Arc<dyn ArticleRepairs>) -> bool {
        self.repairs.set(source).is_ok()
    }
    #[must_use]
    pub fn repair_revision(&self, id: &str) -> RepairRevision {
        self.repairs
            .get()
            .map_or(RepairRevision::Base, |source| source.revision(id))
    }
    #[must_use]
    pub fn repair_generation(&self) -> u64 {
        self.repairs.get().map_or(0, |source| source.generation())
    }

    /// Validates already-cached repaired bytes against the ledger's immutable
    /// geometry without reading its payload or using a provider connection.
    pub fn check_cached_repair(
        &self,
        id: &str,
        revision: RepairRevision,
        begin: Option<u64>,
        length: usize,
    ) -> Result<(), Error> {
        match revision {
            RepairRevision::Base => Ok(()),
            RepairRevision::Pending => Err(Error::Unavailable),
            RepairRevision::Repaired(stamp) => {
                let expected = self
                    .repairs
                    .get()
                    .ok_or(Error::Unavailable)?
                    .expectation(id, stamp)?;
                if begin
                    .zip(expected.offset)
                    .is_some_and(|(wanted, actual)| wanted != actual)
                    || expected
                        .decoded_len
                        .is_some_and(|actual| actual != length as u64)
                {
                    return Err(Error::Yenc(
                        "cached repair does not match expected article geometry",
                    ));
                }
                Ok(())
            }
        }
    }

    /// Requests that skipped a provider because it recently lacked the article.
    #[must_use]
    pub fn missing_hits(&self) -> u64 {
        self.missing.hits()
    }

    /// Display names by provider id, including providers no longer configured.
    #[must_use]
    pub fn provider_names(&self) -> Vec<String> {
        let known = self.known.lock().unwrap_or_else(PoisonError::into_inner);
        known.iter().map(|k| k.name.clone()).collect()
    }

    /// Counters since start for every configured provider.
    #[must_use]
    pub fn stats(&self) -> Vec<ProviderStats> {
        self.snapshot()
            .iter()
            .map(|e| {
                let (connect_failures, trips, workloads) = e.stats.snapshot();
                let pool = &e.provider.pool;
                let (open, idle) = pool.connections();
                let (connections, connecting) = pool.links();
                let (connected, connect_error) = pool.connect_status();
                ProviderStats {
                    id: e.id,
                    key: e.provider.key.clone(),
                    name: e.provider.name.clone(),
                    priority: e.provider.priority,
                    live: pool.live(),
                    busy: pool.busy(),
                    active: open.saturating_sub(idle),
                    max_connections: pool.max_connections(),
                    pipeline_depth: pool.pipeline_depth(),
                    connections,
                    connecting,
                    connect_failures,
                    trips,
                    open_for: e.breaker.open_for(),
                    connected,
                    connect_error,
                    workloads,
                }
            })
            .collect()
    }

    /// Segments a later provider served after an earlier one missed or failed, since
    /// start.
    #[must_use]
    pub fn rescues(&self) -> Vec<Rescue> {
        let rescues = self.rescues.lock().unwrap_or_else(PoisonError::into_inner);
        rescues
            .iter()
            .map(|(&(from, to, reason), &count)| Rescue {
                from,
                to,
                reason,
                count,
            })
            .collect()
    }

    /// Global download permits not in use; `None` without providers.
    #[must_use]
    pub fn free_permits(&self) -> Option<usize> {
        self.snapshot()
            .first()
            .map(|e| e.provider.pool.free_permits())
    }

    /// Open connections across providers, and how many have no request on them.
    #[must_use]
    pub fn connections(&self) -> (usize, usize) {
        self.snapshot()
            .iter()
            .map(|e| e.provider.pool.connections())
            .fold((0, 0), |(open, idle), (o, i)| (open + o, idle + i))
    }

    /// Whether the provider a request would try first has room for a [`hedge`](Self::hedge)
    /// of `size` bytes right now: a permit and an idle or new connection. A snapshot, not
    /// a reservation, and no promise that the provider has the article.
    #[must_use]
    pub fn can_hedge(&self, size: u32) -> bool {
        let providers = self.snapshot();
        providers
            .iter()
            .filter(|entry| {
                !entry.provider.pool.exhausted()
                    && (!entry.breaker.is_open() || entry.breaker.probe_due())
            })
            .min_by_key(|entry| entry.preference())
            .is_some_and(|entry| entry.provider.pool.can_hedge(size))
    }

    /// Uncached foreground callers waiting for providers. Local repaired bodies
    /// and decoded/native cache hits do not consume this signal. Cancelled callers
    /// stop counting even while an abandoned reply drains in the background.
    #[must_use]
    pub fn foreground_pending(&self) -> bool {
        self.foreground.load(Ordering::Acquire) > 0
    }

    /// Connection limit of the first priority level.
    #[must_use]
    pub fn max_connections(&self) -> usize {
        let providers = self.snapshot();
        let level = providers.iter().map(|e| e.provider.priority).min();
        providers
            .iter()
            .filter(|e| Some(e.provider.priority) == level)
            .map(|e| e.provider.pool.max_connections())
            .sum()
    }

    /// Bounded warming window from the first available provider level. Keeps a
    /// quarter of effective connection capacity for other transfers. Pressure
    /// reduces new scheduling to the floor; existing replies drain normally.
    #[must_use]
    pub fn warming_window(&self, floor: usize) -> usize {
        let providers = self.snapshot();
        let available = |e: &&Entry| !e.provider.pool.exhausted() && !e.breaker.is_open();
        let level = providers
            .iter()
            .filter(available)
            .map(|e| e.provider.priority)
            .min();
        let mut total = 0usize;
        let mut free = 0usize;
        let mut held = 0usize;
        let mut pressure = self.foreground_pending();
        for entry in providers
            .iter()
            .filter(available)
            .filter(|e| Some(e.provider.priority) == level)
        {
            let (limit, busy, warm, waiting) = entry.provider.pool.warming_capacity();
            total = total.saturating_add(limit);
            free = free.saturating_add(limit.saturating_sub(busy));
            held = held.saturating_add(warm);
            pressure |= waiting;
        }
        warm_target(floor, held, total, free, pressure)
    }

    fn snapshot(&self) -> Arc<[Entry]> {
        Arc::clone(
            &self
                .providers
                .read()
                .unwrap_or_else(PoisonError::into_inner),
        )
    }

    /// Opens up to `connections` across the first healthy level, split by connection
    /// limits, so a stream's first reads skip the handshakes.
    pub async fn prewarm(&self, connections: usize) {
        self.cancel_lease();
        let providers = self.snapshot();
        let healthy = || {
            providers
                .iter()
                .filter(|e| !e.breaker.is_open() && !e.provider.pool.exhausted())
        };
        let Some(level) = healthy().map(|e| e.provider.priority).min() else {
            return;
        };
        let pools: Vec<_> = healthy()
            .filter(|e| e.provider.priority == level)
            .map(|e| &e.provider.pool)
            .collect();
        let total: usize = pools.iter().map(|p| p.max_connections()).sum();
        join_all(
            pools
                .iter()
                .map(|p| p.prewarm((connections * p.max_connections()).div_ceil(total.max(1)))),
        )
        .await;
    }

    /// Tops each pool up to its warm floor, skipping open circuits and used-up quotas.
    /// Does nothing while a [`Lease`] is held, and a lease waits for it to finish.
    /// Failed opens are left to the pool's connect backoff, not the circuit breaker.
    pub async fn warm(&self) {
        let Ok(_open) = self.gate.try_read() else {
            return;
        };
        let providers = self.snapshot();
        join_all(
            providers
                .iter()
                .filter(|e| {
                    e.provider.pool.min_connections() > 0
                        && !e.breaker.is_open()
                        && !e.provider.pool.exhausted()
                })
                .map(|e| e.provider.pool.prewarm(e.provider.pool.min_connections())),
        )
        .await;
    }

    /// Fetches and decodes one body (message-id without angle brackets); `probe` traces
    /// each provider attempt.
    ///
    /// [`Error::NotFound`] only when every provider (or a backbone sibling) answered
    /// that it lacks the article, or the id is empty (an article the NZB never listed);
    /// otherwise the last provider error, or [`Error::Unavailable`] when open circuits
    /// were skipped. A final corrupt copy becomes [`Error::Unavailable`] when another
    /// provider was skipped or failed.
    pub async fn body(
        &self,
        message_id: &str,
        workload: Workload,
        probe: Option<&Probe>,
    ) -> Result<Body, Error> {
        self.body_with(message_id, workload, probe, None, None)
            .await
    }

    /// [`body`](Self::body), sending decoded bytes to `progress` as they arrive once it is
    /// wanted. With `begin` (the expected `=ypart` offset), a provider serving another
    /// part of the posted file counts as corrupt and the next provider is tried.
    pub async fn body_with(
        &self,
        message_id: &str,
        workload: Workload,
        probe: Option<&Probe>,
        progress: Option<&Progress>,
        begin: Option<u64>,
    ) -> Result<Body, Error> {
        let tag = Tag {
            workload,
            probe: probe.cloned(),
            progress: progress.cloned(),
            begin,
            ..Tag::UNTRACKED
        };
        self.body_tagged(message_id, tag).await
    }

    /// A complete verified BODY for maintenance probes. No streaming callback can
    /// observe bytes before their imported geometry has been checked.
    pub async fn body_checked(
        &self,
        message_id: &str,
        workload: Workload,
        expected: crate::BodyExpectation,
    ) -> Result<Body, Error> {
        self.body_tagged(
            message_id,
            Tag {
                workload,
                begin: expected.offset,
                expected: Some(expected),
                ..Tag::UNTRACKED
            },
        )
        .await
    }

    /// A checked maintenance BODY in one isolated operation lane. Geometry is
    /// validated before the caller or abandoned-body cache sink sees any bytes.
    pub async fn body_checked_in_lane(
        &self,
        message_id: &str,
        lane: Lane,
        workload: Workload,
        expected: crate::BodyExpectation,
    ) -> Result<Body, Error> {
        self.body_tagged(
            message_id,
            Tag {
                lane: Some(lane),
                workload,
                begin: expected.offset,
                expected: Some(expected),
                ..Tag::UNTRACKED
            },
        )
        .await
    }

    /// A BODY belonging to one independently scheduled operation. Requests from the
    /// same lane retain normal pipelining and the global provider/permit limits.
    pub async fn body_in_lane(
        &self,
        message_id: &str,
        lane: Lane,
        workload: Workload,
        probe: Option<&Probe>,
        begin: Option<u64>,
    ) -> Result<Body, Error> {
        self.body_tagged(
            message_id,
            Tag {
                lane: Some(lane),
                workload,
                probe: probe.cloned(),
                begin,
                ..Tag::UNTRACKED
            },
        )
        .await
    }
    /// A STAT in the same connection lane as that operation's BODY requests.
    pub async fn stat_in_lane(
        &self,
        message_id: &str,
        lane: Lane,
        workload: Workload,
        probe: Option<&Probe>,
    ) -> Result<(), Error> {
        self.request(
            Command::Stat,
            message_id,
            Tag {
                lane: Some(lane),
                workload,
                probe: probe.cloned(),
                ..Tag::UNTRACKED
            },
        )
        .await
        .map(drop)
    }

    /// A playback body racing a slow fetch of the same article: waits for an idle
    /// connection rather than pipeline behind another request.
    pub async fn hedge(
        &self,
        message_id: &str,
        probe: Option<&Probe>,
        begin: Option<u64>,
    ) -> Result<Body, Error> {
        let tag = Tag {
            probe: probe.cloned(),
            begin,
            fresh: true,
            ..Tag::UNTRACKED
        };
        self.body_tagged(message_id, tag).await
    }

    async fn body_tagged(&self, message_id: &str, tag: Tag) -> Result<Body, Error> {
        let workload = tag.workload;
        let (body, revision) = self.request(Command::Body, message_id, tag).await?;
        let body = body.ok_or(Error::Protocol)?;
        if let Some(sink) = self.sink.get() {
            sink(message_id, workload, &body, false, revision);
        }
        Ok(body)
    }

    /// Whether any provider has the article, with [`body`](Self::body)'s cascade, error
    /// semantics and tracing.
    pub async fn stat(
        &self,
        message_id: &str,
        workload: Workload,
        probe: Option<&Probe>,
    ) -> Result<(), Error> {
        let tag = Tag {
            workload,
            probe: probe.cloned(),
            ..Tag::UNTRACKED
        };
        self.request(Command::Stat, message_id, tag).await.map(drop)
    }

    /// Takes a [`Lease`] once every admitted request has finished and closes the idle
    /// connections of provider `close`, so a diagnostic can open its own within the
    /// provider's limit. `None` if work is still running after `drain` or playback
    /// started meanwhile.
    pub async fn exclusive(&self, close: &str, drain: Duration) -> Option<Lease> {
        let gate = Arc::clone(&self.gate).write_owned().await;
        let token = CancellationToken::new();
        *self.preempt.lock().unwrap_or_else(PoisonError::into_inner) = Some(token.clone());
        let lease = Lease {
            _gate: gate,
            token,
            preempt: Arc::clone(&self.preempt),
        };
        let deadline = Instant::now() + drain;
        let providers = self.snapshot();
        while self.admitted.load(Ordering::SeqCst) > 0
            || providers.iter().any(|e| e.provider.pool.busy() > 0)
        {
            if lease.token.is_cancelled() || Instant::now() >= deadline {
                return None;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        if let Some(entry) = providers.iter().find(|e| e.provider.key == close) {
            let pool = &entry.provider.pool;
            pool.close_idle();
            while pool.live() > 0 && Instant::now() < deadline {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        }
        (!lease.token.is_cancelled()).then_some(lease)
    }

    fn cancel_lease(&self) {
        if let Some(token) = &*self.preempt.lock().unwrap_or_else(PoisonError::into_inner) {
            token.cancel();
        }
    }

    /// Playback preempts a [`Lease`]; other work waits for it.
    async fn admit(&self, workload: Workload) -> Load {
        if workload == Workload::Stream {
            self.cancel_lease();
            return Load::acquire(&self.admitted);
        }
        // Counted before the gate opens again, so a lease sees it.
        let _open = self.gate.read().await;
        Load::acquire(&self.admitted)
    }

    async fn request(
        &self,
        command: Command,
        message_id: &str,
        mut tag: Tag,
    ) -> Result<(Option<Body>, RepairRevision), Error> {
        if tag.workload == Workload::Warm {
            tag.wire = self.warm_wire.get().cloned();
            tag.lane = Some(self.warm_lane);
        }
        if message_id.is_empty() {
            return Err(Error::NotFound);
        }
        validate_message_id(message_id)?;
        for _ in 0..2 {
            let revision = self.repair_revision(message_id);
            tag.repair_revision = revision;
            let result = match revision {
                RepairRevision::Pending => Err(Error::Unavailable),
                RepairRevision::Base => {
                    self.provider_request(command, message_id, tag.clone())
                        .await
                }
                RepairRevision::Repaired(stamp) => {
                    let Some(source) = self.repairs.get() else {
                        return Err(Error::Unavailable);
                    };
                    self.repaired_request(command, message_id, &tag, source.as_ref(), stamp)
                        .await
                }
            };
            if self.repair_revision(message_id) == revision {
                return result.map(|body| (body, revision));
            }
        }
        Err(Error::Unavailable)
    }

    async fn repaired_request(
        &self,
        command: Command,
        id: &str,
        tag: &Tag,
        source: &dyn ArticleRepairs,
        revision: [u8; 32],
    ) -> Result<Option<Body>, Error> {
        let body = match source.body(id, revision).await {
            Ok(body) => body,
            Err(Error::NotFound) => {
                let expected = source.expectation(id, revision)?;
                let mut fallback = tag.clone();
                fallback.expected = Some(expected);
                fallback.expected_hash = Some(source.expected_hash(id, revision)?);
                // Full hash proof must precede every delivery, including abandoned
                // sinks. STAT also needs a BODY; mere article presence is no proof.
                fallback.progress = None;
                fallback.begin = expected.offset;
                self.provider_request(Command::Body, id, fallback)
                    .await?
                    .ok_or(Error::Protocol)?
            }
            Err(error) => return Err(error),
        };
        if body.data.len() > crate::article::MAX_DECODED_LEN {
            return Err(Error::TooLarge);
        }
        if let Some(expected) = tag.expected {
            expected.validate(&body)?;
        } else if tag.begin.is_some_and(|begin| body.offset != begin) {
            return Err(Error::Yenc("repair does not match expected article offset"));
        }
        Ok((command == Command::Body).then_some(body))
    }

    #[allow(clippy::significant_drop_tightening)] // False positive: `order` holds only references.
    async fn provider_request(
        &self,
        command: Command,
        message_id: &str,
        base: Tag,
    ) -> Result<Option<Body>, Error> {
        if message_id.is_empty() {
            return Err(Error::NotFound);
        }
        validate_message_id(message_id)?;
        let workload = base.workload;
        let _foreground = (workload == Workload::Stream).then(|| Load::acquire(&self.foreground));
        let _admitted = self.admit(workload).await;
        let providers = self.snapshot();
        if providers.is_empty() {
            return Err(Error::Unavailable);
        }
        let mut order: Vec<_> = providers.iter().collect();
        order.sort_by_cached_key(|entry| entry.preference());

        let mut last_error = None;
        let mut skipped = false;
        // Some attempt ended in a provider failure rather than an answer.
        let mut failed = false;
        // The first provider that missed or failed, for rescue counts.
        let mut first_miss: Option<(u16, trace::Outcome)> = None;
        // Backbones whose stored copy is missing or corrupt.
        let mut missed: Vec<&str> = Vec::new();
        // When every circuit is open, try them anyway: the fault is likely ours.
        for force in [false, true] {
            let mut attempted = false;
            for entry in &order {
                let backbone = entry.provider.backbone.as_deref();
                if backbone.is_some_and(|b| missed.contains(&b)) {
                    continue;
                }
                let group = backbone.unwrap_or(&entry.provider.key);
                // Never forced: a used-up quota is the user's limit, not a fault.
                if entry.provider.pool.exhausted() {
                    skipped = true;
                    continue;
                }
                if base.expected_hash.is_none() && self.missing.contains(group, message_id) {
                    missed.extend(backbone);
                    first_miss.get_or_insert((entry.id, trace::Outcome::NotFound));
                    continue;
                }
                let Some(attempt) = entry.breaker.attempt(force) else {
                    skipped = true;
                    continue;
                };
                attempted = true;
                let tag = Tag {
                    provider: entry.id,
                    stats: Some(Arc::clone(&entry.stats)),
                    sink: Some(Arc::clone(&self.sink)),
                    ..base.clone()
                };
                let result = entry.provider.pool.fetch(command, message_id, &tag).await;
                if matches!(&result, Err(Failure::Request(error)) if error.is_wire_budget()) {
                    return Err(Error::Io(crate::wire::Denied.into()));
                }
                // No slot in time says nothing about the provider: the attempt drops unrecorded.
                if matches!(result, Err(Failure::Request(Error::Unavailable))) {
                    skipped = true;
                    continue;
                }
                if let Err(Failure::Request(error @ (Error::Status { .. } | Error::Protocol))) =
                    &result
                {
                    let code = match error {
                        Error::Status { code, .. } => Some(*code),
                        _ => None,
                    };
                    if let Some(suppressed) = entry.stats.unexpected(code, Instant::now()) {
                        tracing::warn!(
                            provider = entry.provider.name.as_str(),
                            reason = %error,
                            suppressed,
                            "provider sent an unexpected reply; trying the next provider"
                        );
                    }
                }
                let (outcome, error) = match result {
                    Ok(body) => {
                        // Only a delivered body proves the provider fully healthy.
                        attempt.finish(if body.is_some() {
                            Outcome::Success
                        } else {
                            Outcome::Miss
                        });
                        if let Some((from, reason)) = first_miss {
                            *self
                                .rescues
                                .lock()
                                .unwrap_or_else(PoisonError::into_inner)
                                .entry((from, entry.id, reason))
                                .or_default() += 1;
                        }
                        return Ok(body);
                    }
                    Err(Failure::Request(Error::NotFound)) => (Outcome::Miss, None),
                    Err(Failure::Request(
                        error @ (Error::CrcMismatch { .. } | Error::Yenc(_) | Error::TooLarge),
                    )) => (Outcome::Miss, Some(error)),
                    Err(
                        Failure::Connect(error)
                        | Failure::Request(error @ Error::Status { code: 502, .. }),
                    ) => (Outcome::Fatal, Some(error)),
                    Err(Failure::Request(error)) => (Outcome::Failure, Some(error)),
                };
                failed |= matches!(outcome, Outcome::Failure | Outcome::Fatal);
                if matches!(outcome, Outcome::Miss) {
                    missed.extend(backbone);
                    if error.is_none() {
                        self.missing.insert(group, message_id);
                    }
                }
                first_miss.get_or_insert_with(|| {
                    let reason = error
                        .as_ref()
                        .map_or(trace::Outcome::NotFound, stats::outcome);
                    (entry.id, reason)
                });
                if attempt.finish(outcome) {
                    entry.stats.tripped();
                }
                last_error = error.map(|e| (e, &entry.stats)).or(last_error);
            }
            if attempted || !skipped || force {
                break;
            }
            skipped = false;
        }
        // ponytail: a request that never reached a provider (all skipped) is not
        // counted; give the client its own counter if quota cut-offs need showing.
        Err(match last_error {
            // A skipped or failed provider may hold an intact copy.
            Some((Error::CrcMismatch { .. } | Error::Yenc(_) | Error::TooLarge, stats))
                if skipped || failed =>
            {
                stats.failed(workload);
                Error::Unavailable
            }
            Some((error, stats)) => {
                stats.failed(workload);
                error
            }
            None if skipped => Error::Unavailable,
            None => Error::NotFound,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Downloads, Endpoint, PoolConfig, TlsMode};
    use infinidysk_testkit::{Corpus, LoopbackServer};

    #[test]
    fn warm_capacity_keeps_a_quarter_reserve_and_shrinks_without_revoking_held_replies() {
        assert_eq!(warm_target(2, 4, 40, 30, false), 24);
        assert_eq!(warm_target(2, 4, 40, 10, false), 4);
        assert_eq!(warm_target(2, 12, 40, 28, true), 2);
        assert_eq!(warm_target(3, 0, 8, 0, false), 3);
        assert_eq!(warm_target(3, 0, 0, 0, false), 3);
        assert_eq!(warm_target(2, 0, 400, 400, false), 24);
        assert_eq!(warm_target(2, 0, 4, 4, false), 3);
        assert_eq!(warm_target(99, 0, 0, 0, true), 8);
    }

    #[allow(clippy::unwrap_used)] // Test helper; panics are the assertions.
    fn provider(server: &LoopbackServer, downloads: &Arc<Downloads>) -> Provider {
        let port = server.addr().port();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            port,
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections: 2,
            pipeline_depth: 1,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        };
        Provider {
            pool: Pool::new(endpoint, config, Arc::clone(downloads)).unwrap(),
            key: port.to_string(),
            name: port.to_string(),
            priority: 0,
            backbone: None,
        }
    }

    #[tokio::test]
    async fn a_stat_probe_closes_without_resetting_the_back_off() {
        let corpus = Corpus::generate(1025, 1, 1024);
        let server = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let client = Client::new([provider(&server, &Downloads::new(4))]);
        let providers = client.snapshot();
        let breaker = &providers[0].breaker;
        let trip = || breaker.attempt(true).unwrap().finish(Outcome::Fatal);

        assert!(trip());
        breaker.lapse();
        client
            .stat(&corpus.articles[0].message_id, Workload::Health, None)
            .await
            .unwrap();
        assert!(!breaker.is_open());
        assert!(trip());
        assert!(breaker.open_for().unwrap() > Duration::from_secs(60));
    }

    #[tokio::test]
    async fn a_tripped_provider_is_probed_once_its_cooldown_ends() {
        let corpus = Corpus::generate(1025, 1, 1024);
        let peer = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let tripped = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let downloads = Downloads::new(4);
        let client = Client::new([provider(&peer, &downloads), provider(&tripped, &downloads)]);
        let providers = client.snapshot();
        let breaker = &providers[1].breaker;
        assert!(breaker.attempt(true).unwrap().finish(Outcome::Fatal));
        breaker.lapse();

        client
            .body(&corpus.articles[0].message_id, Workload::Stream, None)
            .await
            .unwrap();
        assert_eq!((tripped.body_commands(), peer.body_commands()), (1, 0));
        assert!(!breaker.is_open());
    }

    #[tokio::test]
    async fn a_corrupt_copy_is_not_final_while_a_provider_was_skipped() {
        let corpus = Corpus::generate(1025, 1, 1024);
        let mut corrupt = corpus.articles[0].clone();
        let at = corrupt
            .wire
            .windows(7)
            .position(|w| w == b"pcrc32=")
            .unwrap()
            + 7;
        corrupt.wire[at] = if corrupt.wire[at] == b'0' { b'1' } else { b'0' };
        let bad = LoopbackServer::start([corrupt]).await.unwrap();
        let skipped = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let downloads = Downloads::new(4);
        let client = Client::new([provider(&skipped, &downloads), provider(&bad, &downloads)]);
        let providers = client.snapshot();
        assert!(
            providers[0]
                .breaker
                .attempt(true)
                .unwrap()
                .finish(Outcome::Fatal)
        );

        let result = client
            .body(&corpus.articles[0].message_id, Workload::Stream, None)
            .await;
        assert!(
            matches!(result, Err(Error::Unavailable)),
            "{:?}",
            result.err()
        );
        assert_eq!(bad.body_commands(), 1);
    }

    #[tokio::test]
    async fn a_corrupt_copy_is_not_final_after_another_provider_connection_failed() {
        let corpus = Corpus::generate(1025, 1, 1024);
        let mut corrupt = corpus.articles[0].clone();
        let at = corrupt
            .wire
            .windows(7)
            .position(|w| w == b"pcrc32=")
            .expect("CRC field")
            + 7;
        corrupt.wire[at] = if corrupt.wire[at] == b'0' { b'1' } else { b'0' };
        let bad = LoopbackServer::start([corrupt])
            .await
            .expect("bad provider");
        let failed = LoopbackServer::start_dropping(corpus.articles.clone(), 0)
            .await
            .expect("failed provider");
        let downloads = Downloads::new(4);
        let first = provider(&failed, &downloads);
        let mut second = provider(&bad, &downloads);
        second.priority = 1;
        let client = Client::new([first, second]);
        assert!(matches!(
            client
                .body(&corpus.articles[0].message_id, Workload::Health, None)
                .await,
            Err(Error::Unavailable)
        ));
        assert_eq!(bad.body_commands(), 1);
        assert_eq!(downloads.available_permits(), 4);
    }

    #[tokio::test]
    async fn foreground_demand_excludes_background_work_and_cancelled_callers() {
        for workload in [Workload::Stream, Workload::Health] {
            let corpus = Corpus::generate(1025, 1, 1024);
            let id = corpus.articles[0].message_id.clone();
            let (server, mut gate) = LoopbackServer::start_gated(corpus.articles)
                .await
                .expect("server");
            let downloads = Downloads::new(4);
            let client = Arc::new(Client::new([provider(&server, &downloads)]));
            assert!(!client.foreground_pending());
            let task = {
                let client = client.clone();
                tokio::spawn(async move { client.body(&id, workload, None).await })
            };
            tokio::time::timeout(Duration::from_secs(5), gate.blocked())
                .await
                .expect("admitted");
            assert_eq!(client.foreground_pending(), workload == Workload::Stream);
            task.abort();
            assert!(task.await.expect_err("caller cancelled").is_cancelled());
            assert!(
                !client.foreground_pending(),
                "the still-draining old response is not new playback demand"
            );
            gate.release();
            tokio::time::timeout(Duration::from_secs(5), async {
                while downloads.available_permits() != 4 {
                    tokio::task::yield_now().await;
                }
            })
            .await
            .expect("drain releases permits");
        }
    }

    #[tokio::test]
    async fn a_stream_read_gives_up_without_a_download_slot() {
        let corpus = Corpus::generate(1025, 1, 1024);
        let server = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let downloads = Downloads::new(1);
        let held = downloads.acquire(false, 1).await;
        let mut provider = provider(&server, &downloads);
        provider.pool = provider.pool.with_stream_wait(Duration::from_millis(100));
        let client = Client::new([provider]);
        let providers = client.snapshot();
        let breaker = &providers[0].breaker;
        assert!(breaker.attempt(true).unwrap().finish(Outcome::Fatal));
        breaker.lapse();

        let result = client
            .body(&corpus.articles[0].message_id, Workload::Stream, None)
            .await;
        assert!(matches!(result, Err(Error::Unavailable)));
        assert!(
            breaker.probe_due(),
            "the probe recorded nothing and freed its slot"
        );
        drop(held);
    }
    #[tokio::test]
    async fn warming_wire_exhaustion_is_exact_and_does_not_trip_providers_or_charge_playback() {
        let corpus = Corpus::generate(1025, 1, 32768);
        let id = corpus.articles[0].message_id.clone();
        let server = LoopbackServer::start(corpus.articles)
            .await
            .expect("server");
        let downloads = Downloads::new(4);
        let client = Client::new([provider(&server, &downloads)]);
        let charged = Arc::new(AtomicU64::new(0));
        let budget = crate::wire::Budget::new(Arc::new({
            let charged = charged.clone();
            move |_, requested, limit| {
                let charged = charged.clone();
                Box::pin(async move {
                    let old = charged.load(Ordering::SeqCst);
                    let grant = requested.min(limit.saturating_sub(old));
                    if grant == 0 {
                        return Err(crate::wire::Denied);
                    }
                    charged.fetch_add(grant, Ordering::SeqCst);
                    Ok(grant)
                })
            }
        }));
        budget.configure(true, 4096);
        assert!(client.set_warming_budget(budget.clone()));
        let error = client
            .body(&id, Workload::Warm, None)
            .await
            .expect_err("wire cap");
        assert!(error.is_wire_budget());
        assert_eq!(budget.received(), 4096);
        assert_eq!(charged.load(Ordering::SeqCst), 4096);
        assert!(!client.snapshot()[0].breaker.is_open());
        let body = client
            .body(&id, Workload::Stream, None)
            .await
            .expect("playback remains independent");
        assert_eq!(body.data.len(), 32768);
        assert_eq!(budget.received(), 4096);
        assert_eq!(downloads.available_permits(), 4);
    }
    #[tokio::test]
    async fn warming_wire_observes_full_protocol_response_including_abandoned_body_drain() {
        let corpus = Corpus::generate(1025, 1, 32768);
        let id = corpus.articles[0].message_id.clone();
        let expected =
            corpus.articles[0].wire.len() + format!("222 0 <{id}> body follows\r\n").len();
        let (server, mut gate) = LoopbackServer::start_gated(corpus.articles)
            .await
            .expect("server");
        let downloads = Downloads::new(4);
        let client = Arc::new(Client::new([provider(&server, &downloads)]));
        let budget = crate::wire::Budget::new(Arc::new(|_, requested, _| {
            Box::pin(async move { Ok(requested) })
        }));
        budget.configure(true, 1_000_000);
        assert!(client.set_warming_budget(budget.clone()));
        let task = {
            let client = client.clone();
            tokio::spawn(async move { client.body(&id, Workload::Warm, None).await })
        };
        tokio::time::timeout(Duration::from_secs(5), gate.blocked())
            .await
            .expect("request started");
        task.abort();
        let _ = task.await;
        gate.release();
        tokio::time::timeout(Duration::from_secs(5), async {
            while downloads.available_permits() != 4 {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("drain");
        assert_eq!(
            budget.received(),
            expected as u64,
            "cancelled callers cannot erase bytes received by the draining transport"
        );
    }
}
