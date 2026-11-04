//! One durable PAR2 lane. Health only admits work. Publishing journals are observed
//! before any retry, and completion commits with urgent source verification.
use super::source;
use anyhow::{Context as _, ensure};
use infinidysk_core::ItemId;
use infinidysk_db::{
    Db, HealthState,
    attempts::{Attempt, Completion},
    now_ms,
};
use infinidysk_health::{
    Health,
    par2::{self, ArticleOutcome, Budget, DiscoveryBudget},
};
use infinidysk_native_cache::repair::{
    Error as StoreError, Geometry, Patch, Proof, Revision, Store,
};
use infinidysk_nntp::{ArticleRepairs as _, Client};
use infinidysk_vfs::Vfs;
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use std::{
    sync::{Arc, OnceLock, Weak},
    time::Duration,
};
use tokio::sync::Mutex;
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;
use uuid::Uuid;
const SECOND: i64 = 1000;
const MAX_PAYLOAD: usize = 1 << 20;
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=Par2Settings)]
pub struct Settings {
    pub enabled: bool,
    pub max_missing_slices: u8,
    pub memory_mb: u32,
    pub failure_cooldown_hours: u16,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: false,
            max_missing_slices: 8,
            memory_mb: 256,
            failure_cooldown_hours: 6,
        }
    }
}
impl Settings {
    pub fn validate(self) -> anyhow::Result<()> {
        ensure!(
            (1..=64).contains(&self.max_missing_slices),
            "PAR2 missing-slice limit must be 1 to 64"
        );
        ensure!(
            (128..=2048).contains(&self.memory_mb),
            "PAR2 working-memory allowance must be 128 to 2048 MB"
        );
        ensure!(
            (1..=168).contains(&self.failure_cooldown_hours),
            "PAR2 failure cooldown must be 1 to 168 hours"
        );
        Ok(())
    }
    fn budget(self) -> DiscoveryBudget {
        let mut execution = Budget::default();
        execution.reconstruction.missing = usize::from(self.max_missing_slices);
        execution.reconstruction.memory = self.memory_mb as usize * 1_000_000;
        DiscoveryBudget {
            execution,
            ..DiscoveryBudget::default()
        }
    }
    fn cooldown(self) -> i64 {
        i64::from(self.failure_cooldown_hours) * 3_600 * SECOND
    }
}
#[derive(Clone, Serialize, Deserialize)]
struct Receipt {
    id: String,
    revision: [u8; 32],
    hash: [u8; 32],
    geometry: Geometry,
    length: u64,
}
#[derive(Clone, Serialize, Deserialize)]
struct Payload {
    version: u8,
    faults: [u8; 32],
    policy: Settings,
    attempts: u32,
    run: Uuid,
    phase: String,
    reason: Option<String>,
    manifest: Vec<Receipt>,
}
impl Payload {
    fn load(row: &Attempt) -> anyhow::Result<Self> {
        let result: Self =
            serde_json::from_str(&row.payload).context("repair attempt payload is invalid")?;
        ensure!(
            result.version == 1 && result.manifest.len() <= 1024,
            "unsupported repair attempt payload"
        );
        ensure!(
            matches!(
                result.phase.as_str(),
                "queued" | "running" | "healthy" | "patches" | "done"
            ),
            "unknown repair attempt phase"
        );
        ensure!(
            result
                .manifest
                .iter()
                .all(|r| !r.id.is_empty() && r.id.len() <= 4096),
            "invalid repair publication receipt"
        );
        Ok(result)
    }
    fn encode(&self) -> anyhow::Result<String> {
        let raw = serde_json::to_string(self)?;
        ensure!(
            raw.len() <= MAX_PAYLOAD,
            "repair attempt journal allowance exceeded"
        );
        Ok(raw)
    }
}
#[derive(Serialize, ToSchema)]
#[schema(as=Par2Attempt)]
pub struct Summary {
    pub id: Uuid,
    pub item: Uuid,
    pub state: String,
    pub urgent: bool,
    pub next_attempt: i64,
    pub updated_at: i64,
    pub attempts: u32,
    pub reason: Option<String>,
    pub patch_articles: usize,
    pub patch_bytes: u64,
}
type Busy = Arc<dyn Fn() -> bool + Send + Sync>;
pub struct Worker {
    db: Db,
    client: Arc<Client>,
    vfs: Arc<Vfs>,
    store: Arc<Store>,
    step: Mutex<u8>,
    health: OnceLock<Weak<Health>>,
    busy: OnceLock<Busy>,
}
impl Worker {
    pub async fn load(
        db: Db,
        client: Arc<Client>,
        vfs: Arc<Vfs>,
        store: Arc<Store>,
    ) -> anyhow::Result<Arc<Self>> {
        let writing = db.write_permit().await;
        db.recover_running_repair_attempts(now_ms()).await?;
        drop(writing);
        Ok(Arc::new(Self {
            db,
            client,
            vfs,
            store,
            step: Mutex::new(0),
            health: OnceLock::new(),
            busy: OnceLock::new(),
        }))
    }
    pub fn bind_health(&self, health: &Arc<Health>) -> bool {
        self.health.set(Arc::downgrade(health)).is_ok()
    }
    pub fn set_busy(&self, busy: Busy) -> bool {
        self.busy.set(busy).is_ok()
    }
    fn busy(&self) -> bool {
        self.client.foreground_pending() || self.busy.get().is_some_and(|busy| busy())
    }
    pub async fn status(&self) -> anyhow::Result<Vec<Summary>> {
        self.db
            .repair_attempts(128)
            .await?
            .into_iter()
            .map(|row| {
                let data = Payload::load(&row)?;
                Ok(Summary {
                    id: row.id,
                    item: row.item_id,
                    state: row.state,
                    urgent: row.priority == 0,
                    next_attempt: row.next_attempt,
                    updated_at: row.updated_at,
                    attempts: data.attempts,
                    reason: data.reason,
                    patch_articles: data.manifest.len(),
                    patch_bytes: data.manifest.iter().map(|r| r.length).sum(),
                })
            })
            .collect()
    }
    /// Health callers already own write admission. This only queues/observes data.
    pub async fn offer(&self, item: ItemId, settings: Settings) -> anyhow::Result<bool> {
        if !settings.enabled {
            return Ok(false);
        }
        settings.validate()?;
        let Some(scope) = source::scope(&self.db, item).await? else {
            return Ok(false);
        };
        if !scope.damaged || scope.item.health_state == HealthState::Hidden {
            return Ok(false);
        }
        if !matches!(
            scope.item.health_state,
            HealthState::Suspect | HealthState::Repairing | HealthState::ActionNeeded
        ) {
            return Ok(true);
        }
        let priority = i16::from(!self.db.health_is_urgent(item).await?);
        let now = now_ms();
        if let Some(row) = self.db.repair_attempt(item, &scope.digest).await? {
            let data = Payload::load(&row)?;
            if matches!(
                row.state.as_str(),
                "queued" | "running" | "publishing" | "deferred"
            ) {
                let reader_ready = row.state == "deferred"
                    && data.reason.as_deref() == Some("waiting for active readers")
                    && !self.vfs.sessions().open().iter().any(|s| s.item == item);
                if priority < row.priority || reader_ready {
                    self.db
                        .update_repair_attempt(
                            &row,
                            &row.state,
                            priority.min(row.priority),
                            if reader_ready { now } else { row.next_attempt },
                            now,
                            &row.payload,
                        )
                        .await?;
                }
                return Ok(true);
            }
            if matches!(row.state.as_str(), "unsupported" | "limit" | "healthy")
                && row.next_attempt > now
                && data.faults == scope.faults
                && data.policy == settings
            {
                return Ok(false);
            }
            let payload = Payload {
                version: 1,
                faults: scope.faults,
                policy: settings,
                attempts: data.attempts,
                run: Uuid::nil(),
                phase: "queued".into(),
                reason: None,
                manifest: vec![],
            }
            .encode()?;
            self.db
                .update_repair_attempt(&row, "queued", priority, now, now, &payload)
                .await?;
            return Ok(true);
        }
        let payload = Payload {
            version: 1,
            faults: scope.faults,
            policy: settings,
            attempts: 0,
            run: Uuid::nil(),
            phase: "queued".into(),
            reason: None,
            manifest: vec![],
        }
        .encode()?;
        self.db
            .enqueue_repair_attempt(item, &scope.digest, priority, now, &payload)
            .await?;
        // Backpressure defers replacement too; no unjournaled attempt may start.
        Ok(true)
    }
    async fn update(
        &self,
        row: &Attempt,
        state: &str,
        data: &Payload,
        next: i64,
    ) -> anyhow::Result<Option<Attempt>> {
        let raw = data.encode()?;
        if !self
            .db
            .update_repair_attempt(row, state, row.priority, next, now_ms(), &raw)
            .await?
        {
            return Ok(None);
        }
        self.db
            .repair_attempt(
                ItemId(row.item_id),
                &row.source
                    .as_slice()
                    .try_into()
                    .context("repair source digest")?,
            )
            .await
            .map_err(Into::into)
    }
    async fn disposition(
        &self,
        row: &Attempt,
        state: &str,
        reason: &str,
        next: i64,
    ) -> anyhow::Result<bool> {
        let _writing = self.db.write_permit().await;
        let Some(current) = self
            .db
            .repair_attempt(
                ItemId(row.item_id),
                &row.source
                    .as_slice()
                    .try_into()
                    .context("repair source digest")?,
            )
            .await?
        else {
            return Ok(false);
        };
        let mut data = Payload::load(&current)?;
        if data.run != Payload::load(row)?.run {
            return Ok(false);
        }
        data.reason = Some(reason.into());
        self.update(&current, state, &data, next).await?;
        Ok(true)
    }
    pub async fn tick(
        &self,
        settings: Settings,
        cancel: CancellationToken,
    ) -> anyhow::Result<bool> {
        let Ok(mut step) = self.step.try_lock() else {
            return Ok(false);
        };
        let row = {
            let _writing = self.db.write_permit().await;
            self.db.recover_running_repair_attempts(now_ms()).await?;
            self.db
                .next_repair_attempt_fair(now_ms(), *step >= 4)
                .await?
        };
        let Some(row) = row else {
            return Ok(false);
        };
        if row.state == "publishing" {
            return self.readback(&row).await;
        }
        if self.store.is_pending() {
            let _writing = self.db.write_permit().await;
            if self.store.reconcile().await.is_err() {
                return Ok(false);
            }
        }
        if !settings.enabled || self.busy() {
            return Ok(false);
        }
        settings.validate()?;
        ensure!(
            self.health.get().and_then(Weak::upgrade).is_some(),
            "repair health integration is unavailable"
        );
        let item = ItemId(row.item_id);
        let Some(scope) = source::scope(&self.db, item).await? else {
            return self
                .disposition(
                    &row,
                    "superseded",
                    "source metadata is no longer available",
                    now_ms(),
                )
                .await;
        };
        if scope.digest.as_slice() != row.source || scope.item.health_state == HealthState::Hidden {
            return self
                .disposition(
                    &row,
                    "superseded",
                    "source identity or replacement state changed",
                    now_ms(),
                )
                .await;
        }
        if !scope.damaged {
            return self
                .disposition(
                    &row,
                    "healthy",
                    "damage was cleared by another health check",
                    now_ms(),
                )
                .await;
        }
        if self.vfs.sessions().open().iter().any(|s| s.item == item) {
            return self
                .disposition(
                    &row,
                    "deferred",
                    "waiting for active readers",
                    now_ms() + 5 * SECOND,
                )
                .await;
        }
        if let Some(cache) = self.vfs.native_cache() {
            let verified = tokio::time::timeout(Duration::from_secs(5), async {
                let key = self.vfs.cache_key(&scope.item).await.ok()?;
                cache.verify_and_protect(&key).await.ok()?
            })
            .await
            .ok()
            .flatten();
            match verified {
                Some(true) => {
                    return self
                        .disposition(
                            &row,
                            "unsupported",
                            "complete verified native copy retained",
                            now_ms() + settings.cooldown(),
                        )
                        .await;
                }
                Some(false) => {}
                None => {
                    return self
                        .disposition(
                            &row,
                            "deferred",
                            "native copy verification unavailable; source retained",
                            now_ms() + 60 * SECOND,
                        )
                        .await;
                }
            }
        }
        let mut data = Payload::load(&row)?;
        data.run = Uuid::new_v4();
        data.phase = "running".into();
        data.reason = None;
        data.manifest.clear();
        data.attempts = data.attempts.saturating_add(1);
        data.policy = settings;
        let row = {
            let _writing = self.db.write_permit().await;
            self.update(&row, "running", &data, now_ms()).await?
        };
        let Some(row) = row else {
            return Ok(true);
        };
        *step = if row.priority == 0 {
            step.saturating_add(1)
        } else {
            0
        };
        let input = match source::input(&self.db, &scope).await {
            Ok(input) => input,
            Err(error)
                if error
                    .downcast_ref::<infinidysk_core::blob::Error>()
                    .is_some()
                    || error
                        .downcast_ref::<infinidysk_queue::nzb::Error>()
                        .is_some() =>
            {
                return self
                    .disposition(
                        &row,
                        "unsupported",
                        "original repair metadata is invalid or exceeds its allowance",
                        now_ms() + settings.cooldown(),
                    )
                    .await;
            }
            Err(_) => {
                return self
                    .disposition(
                        &row,
                        "deferred",
                        "source metadata unavailable; retrying without replacement",
                        now_ms() + 300 * SECOND,
                    )
                    .await;
            }
        };
        let Some(input) = input else {
            return self
                .disposition(
                    &row,
                    "unsupported",
                    "original NZB is unavailable for a verified repair",
                    now_ms() + settings.cooldown(),
                )
                .await;
        };
        if input.anchors.is_empty() {
            return self
                .disposition(
                    &row,
                    "unsupported",
                    "source has no imported PAR2 proof",
                    now_ms() + settings.cooldown(),
                )
                .await;
        }
        let child = cancel.child_token();
        let _stop = child.clone().drop_guard();
        let run = async {
            let plan = par2::discover(
                self.client.clone(),
                input.posts,
                input.anchors,
                settings.budget(),
                child.clone(),
            )
            .await?;
            plan.execute_articles(self.client.clone(), child.clone())
                .await
        };
        let result = tokio::select! {
            ()=cancel.cancelled()=>return Ok(false),
            ()=async {loop {tokio::time::sleep(Duration::from_millis(100)).await;if self.busy(){break;}}}=>{child.cancel();Err(par2::Error::Cancelled)},
            result=tokio::time::timeout(Duration::from_mins(30),run)=>result.unwrap_or(Err(par2::Error::DiscoveryTimeout)),
        };
        match result {
            Ok(ArticleOutcome::Healthy) => {
                data.phase = "healthy".into();
                data.reason = Some("whole-file parity verified; requesting health readback".into());
                let next = {
                    let _writing = self.db.write_permit().await;
                    let Some(current) = self.current_run(&row).await? else {
                        return Ok(true);
                    };
                    self.update(&current, "publishing", &data, now_ms()).await?
                };
                if let Some(row) = next {
                    self.readback(&row).await?;
                }
                Ok(true)
            }
            Ok(ArticleOutcome::Repaired(articles)) => {
                let (patches, receipts) = tokio::task::spawn_blocking(move || {
                    let mut patches = Vec::with_capacity(articles.len());
                    let mut receipts = Vec::with_capacity(articles.len());
                    for article in articles {
                        let (id, body, proof) = article.into_parts();
                        let patch = Patch {
                            message_id: id,
                            geometry: Geometry {
                                offset: body.offset,
                                file_size: body.file_size,
                                part: body.part,
                                total: body.total,
                            },
                            proof: Proof {
                                set: proof.set(),
                                file: proof.file(),
                                file_md5: proof.md5(),
                                file_length: body.file_size,
                            },
                            data: body.data,
                        };
                        receipts.push(Receipt {
                            id: patch.message_id.clone(),
                            revision: patch.expected_revision()?,
                            hash: Sha256::digest(&patch.data).into(),
                            geometry: patch.geometry,
                            length: patch.data.len() as u64,
                        });
                        patches.push(patch);
                    }
                    Ok::<_, StoreError>((patches, receipts))
                })
                .await
                .context("repair publication preparation worker failed")??;
                data.phase = "patches".into();
                data.manifest = receipts;
                data.reason = Some("verified publication intent".into());
                if data.encode().is_err() {
                    return self
                        .disposition(
                            &row,
                            "unsupported",
                            "repair publication journal allowance exceeded",
                            now_ms() + settings.cooldown(),
                        )
                        .await;
                }
                let next = {
                    let writing = self.db.write_permit().await;
                    let ids: Vec<_> = data.manifest.iter().map(|r| r.id.clone()).collect();
                    let Some(_readers) = self.vfs.sessions().try_repair_articles(item, &ids) else {
                        drop(writing);
                        return self
                            .disposition(
                                &row,
                                "deferred",
                                "waiting for active readers",
                                now_ms() + 5 * SECOND,
                            )
                            .await;
                    };
                    let Some(current) = self.current_run(&row).await? else {
                        return Ok(true);
                    };
                    let Some(intent) = self.update(&current, "publishing", &data, now_ms()).await?
                    else {
                        return Ok(true);
                    };
                    let db = self.db.clone();
                    let digest = scope.digest;
                    let nzb = input.nzb;
                    let nzb_hash = input.nzb_hash;
                    let result = self
                        .store
                        .publish_checked(patches, async move {
                            match source::current(&db, item, digest, nzb, nzb_hash).await {
                                Ok(true) => Ok(()),
                                Ok(false) => Err(StoreError::Changed),
                                Err(_) => Err(StoreError::SourceUnavailable),
                            }
                        })
                        .await;
                    match result {
                        Err(StoreError::Budget) if !self.store.is_pending() => {
                            let mut failed = data.clone();
                            failed.reason = Some("repair storage policy allowance exceeded".into());
                            self.update(
                                &intent,
                                "unsupported",
                                &failed,
                                now_ms() + settings.cooldown(),
                            )
                            .await?;
                            None
                        }
                        _ => Some(intent),
                    }
                };
                if let Some(row) = next {
                    self.readback(&row).await?;
                }
                Ok(true)
            }
            Err(par2::Error::MissingLimit { .. }) => {
                self.disposition(
                    &row,
                    "limit",
                    "confirmed damage exceeds the missing-slice policy",
                    now_ms() + settings.cooldown(),
                )
                .await
            }
            Err(par2::Error::Binding | par2::Error::Budget | par2::Error::Recovery(_)) => {
                self.disposition(
                    &row,
                    "unsupported",
                    "PAR2 source binding or resource policy cannot safely repair this source",
                    now_ms() + settings.cooldown(),
                )
                .await
            }
            Err(par2::Error::Cancelled) => {
                self.disposition(
                    &row,
                    "deferred",
                    "repair yielded to foreground work",
                    now_ms() + 5 * SECOND,
                )
                .await
            }
            Err(_) => {
                self.disposition(
                    &row,
                    "deferred",
                    "provider or execution unavailable; urgency retained",
                    now_ms()
                        + if row.priority == 0 {
                            300 * SECOND
                        } else {
                            900 * SECOND
                        },
                )
                .await
            }
        }
    }
    async fn current_run(&self, row: &Attempt) -> anyhow::Result<Option<Attempt>> {
        let current = self
            .db
            .repair_attempt(
                ItemId(row.item_id),
                &row.source
                    .as_slice()
                    .try_into()
                    .context("repair source digest")?,
            )
            .await?;
        let expected = Payload::load(row)?.run;
        match current {
            Some(current) if current.state == row.state => {
                Ok((Payload::load(&current)?.run == expected).then_some(current))
            }
            _ => Ok(None),
        }
    }

    async fn readback(&self, row: &Attempt) -> anyhow::Result<bool> {
        let data = Payload::load(row)?;
        if self.store.is_pending() {
            let writing = self.db.write_permit().await;
            if self.store.reconcile().await.is_err() {
                drop(writing);
                return self
                    .disposition(
                        row,
                        "publishing",
                        "repair publication awaiting durable readback",
                        now_ms() + 5 * SECOND,
                    )
                    .await;
            }
        }
        if data.phase == "patches" {
            ensure!(
                !data.manifest.is_empty(),
                "repair publication manifest is empty"
            );
            let mut matching = true;
            let mut any = false;
            for receipt in &data.manifest {
                match self.store.revision(&receipt.id) {
                    Revision::Base => matching = false,
                    Revision::Pending => {
                        matching = false;
                        any = true;
                    }
                    Revision::Repaired(revision) => {
                        any = true;
                        let same = revision == receipt.revision
                            || (self.store.expected_hash(&receipt.id, revision).ok()
                                == Some(receipt.hash)
                                && self
                                    .store
                                    .expectation(&receipt.id, revision)
                                    .is_ok_and(|e| {
                                        e.offset == Some(receipt.geometry.offset)
                                            && e.file_size == Some(receipt.geometry.file_size)
                                            && e.decoded_len == Some(receipt.length)
                                    }));
                        matching &= same;
                    }
                }
            }
            if !matching {
                return self
                    .disposition(
                        row,
                        if any { "publishing" } else { "deferred" },
                        if any {
                            "publication revisions require further readback"
                        } else {
                            "publication was not accepted; a fresh bounded attempt will retry"
                        },
                        now_ms() + if any { 30 * SECOND } else { 300 * SECOND },
                    )
                    .await;
            }
        } else {
            ensure!(
                data.phase == "healthy",
                "invalid publication readback phase"
            );
        }
        let item = ItemId(row.item_id);
        let Some(scope) = source::scope(&self.db, item).await? else {
            return self
                .disposition(
                    row,
                    "superseded",
                    "source was removed after publication",
                    now_ms(),
                )
                .await;
        };
        if scope.digest.as_slice() != row.source || scope.item.health_state == HealthState::Hidden {
            return self
                .disposition(
                    row,
                    "superseded",
                    "source was replaced after publication",
                    now_ms(),
                )
                .await;
        }
        let Some(health) = self.health.get().and_then(Weak::upgrade) else {
            return self
                .disposition(
                    row,
                    "publishing",
                    "health verification integration unavailable",
                    now_ms() + 5 * SECOND,
                )
                .await;
        };
        let mut done = data.clone();
        done.phase = "done".into();
        done.reason = Some("verified outcome committed with urgent health verification".into());
        let state = if data.phase == "healthy" {
            "healthy"
        } else {
            "repaired"
        };
        let raw = done.encode()?;
        let _writing = self.db.write_permit().await;
        if let Some(current) = self.current_run(row).await? {
            health
                .complete_local_repair(
                    &current,
                    Completion {
                        state,
                        next_attempt: now_ms() + data.policy.cooldown(),
                        now: now_ms(),
                        payload: &raw,
                        blob: scope.item.meta_blob.context("repair metadata identity")?,
                        raw: &scope.raw,
                    },
                )
                .await?;
        }
        Ok(true)
    }
}
