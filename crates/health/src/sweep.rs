//! Durable, sampled content verification. One bounded file yields to foreground work;
//! cursor publication is revision checked and happens only after a completed sample.
use super::{Error, Health, check};
use infinidysk_core::{ItemId, media};
use infinidysk_db::now_ms;
use serde::{Deserialize, Serialize};
use std::time::Duration;
use utoipa::ToSchema;
use uuid::Uuid;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=ContentSweepStatus)]
pub enum Status {
    #[default]
    Idle,
    Running,
    Paused,
    Complete,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=ContentSweepFinding)]
pub struct Finding {
    pub item: Uuid,
    pub outcome: String,
    pub at: i64,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=ContentSweepState)]
pub struct State {
    pub version: u32,
    pub run: Uuid,
    pub status: Status,
    pub category: Option<String>,
    pub limit: Option<u32>,
    pub cursor: Option<Uuid>,
    pub checked: u64,
    pub healthy: u64,
    pub damaged: u64,
    pub unverifiable: u64,
    pub skipped: u64,
    pub samples: u64,
    pub processed_this_run: u32,
    pub started_at: i64,
    pub updated_at: i64,
    pub finished_at: Option<i64>,
    pub recent: Vec<Finding>,
}
impl Default for State {
    fn default() -> Self {
        Self {
            version: 1,
            run: Uuid::nil(),
            status: Status::Idle,
            category: None,
            limit: None,
            cursor: None,
            checked: 0,
            healthy: 0,
            damaged: 0,
            unverifiable: 0,
            skipped: 0,
            samples: 0,
            processed_this_run: 0,
            started_at: 0,
            updated_at: 0,
            finished_at: None,
            recent: Vec::new(),
        }
    }
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=ContentSweep)]
pub struct View {
    pub state: State,
    pub revision: i64,
    pub total: i64,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[serde(tag = "operation", rename_all = "snake_case")]
#[schema(as=ContentSweepOperation)]
pub enum Control {
    Start {
        category: Option<String>,
        limit: Option<u32>,
    },
    Pause,
    Resume,
}
impl Health {
    pub async fn content_sweep(&self) -> Result<View, Error> {
        let row = self.db.content_sweep().await?;
        let state: State = if row.payload == "null" {
            State::default()
        } else {
            serde_json::from_str(&row.payload)?
        };
        if state.version != 1 {
            return Err(Error::Regrab(
                "unsupported sweep journal; preserve it before upgrading",
            ));
        }
        Ok(View {
            state,
            revision: row.revision,
            total: row.total,
        })
    }
    pub async fn control_sweep(&self, revision: i64, control: Control) -> Result<bool, Error> {
        let _gate = self.sweep_gate.lock().await;
        let view = self.content_sweep().await?;
        if view.revision != revision {
            return Ok(false);
        }
        let mut state = view.state;
        let start = matches!(&control, Control::Start { .. });
        match control {
            Control::Start { category, limit } => {
                if state.status == Status::Running {
                    return Err(Error::Regrab(
                        "pause the current sweep before replacing its snapshot",
                    ));
                }
                if category.as_ref().is_some_and(|c| {
                    c.is_empty()
                        || c.len() > 128
                        || c == "."
                        || c == ".."
                        || c.chars().any(|c| c == '/' || c == '\\' || c.is_control())
                }) || limit.is_some_and(|v| v == 0 || v > 10_000_000)
                {
                    return Err(Error::Regrab("invalid sweep category or limit"));
                }
                state = State {
                    run: Uuid::now_v7(),
                    status: Status::Running,
                    category,
                    limit,
                    started_at: now_ms(),
                    ..State::default()
                };
            }
            Control::Pause => {
                if state.status != Status::Running {
                    return Err(Error::Regrab("only a running sweep can pause"));
                }
                state.status = Status::Paused;
            }
            Control::Resume => {
                if state.status != Status::Paused {
                    return Err(Error::Regrab("only a paused sweep can resume"));
                }
                state.status = Status::Running;
                state.processed_this_run = 0;
            }
        }
        state.updated_at = now_ms();
        let payload = serde_json::to_string(&state)?;
        let changed = if start {
            self.db
                .start_content_sweep(revision, &payload, state.category.as_deref())
                .await?
        } else {
            self.db.checkpoint_content_sweep(revision, &payload).await?
        };
        if changed {
            self.sweep_cancel
                .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
            self.wake.notify_one();
        }
        Ok(changed)
    }
    pub(super) fn sweep_step(&self) -> futures_util::future::BoxFuture<'_, Result<bool, Error>> {
        Box::pin(async move {
            if (self.importing)() || (self.streaming)() {
                return Ok(false);
            }
            let (view, mut cancel, mut priority) = {
                let _gate = self.sweep_gate.lock().await;
                (
                    self.content_sweep().await?,
                    self.sweep_cancel.subscribe(),
                    self.priority_cancel.subscribe(),
                )
            };
            if view.state.status != Status::Running {
                return Ok(false);
            }
            let mut state = view.state;
            let next = self.db.next_sweep_item(state.cursor.map(ItemId)).await?;
            let Some(id) = next else {
                state.status = Status::Complete;
                state.finished_at = Some(now_ms());
                self.finish_sweep(view.revision, &mut state).await?;
                return Ok(true);
            };
            let mut outcome = "skipped";
            let mut sample_count = 0;
            if let Some(item) = self.db.item(id).await?
                && media::is_checkable(&item.name)
            {
                outcome = "unverifiable";
                // Invalid or removed metadata cannot stall a whole library.
                if let Ok(Some((_, _, meta))) = self.meta_snapshot(&item).await {
                    let identity = meta.content_identity()?;
                    let probe = async {
                        tokio::select! {
                            result=tokio::time::timeout(Duration::from_secs(25),check::import_sample(&self.client,&meta))=>Some(result),
                            ()=async {loop {tokio::time::sleep(Duration::from_millis(100)).await;if (self.importing)() || (self.streaming)() {return;}}}=>None,
                        }
                    };
                    let result = tokio::select! {result=probe=>result,_=cancel.changed()=>return Ok(true),_=priority.changed()=>return Ok(true)};
                    let Some(result) = result else {
                        return Ok(false);
                    };
                    // Pause/start cannot race with publication of findings or damage.
                    let _gate = self.sweep_gate.lock().await;
                    if self.content_sweep().await?.revision != view.revision {
                        return Ok(true);
                    }
                    if let Ok(sample) = result {
                        sample_count = sample.sampled;
                        if !self
                            .mark_current(&item, Some(&identity), &sample.missing, &sample.corrupt)
                            .await?
                        {
                            outcome = "skipped";
                        } else if !sample.missing.is_empty() || !sample.corrupt.is_empty() {
                            outcome = "damaged";
                            // Use the ordinary confirmation/repair policy, including cached
                            // copies, reader holds, and the auto-repair setting.
                            if !self
                                .report_source(
                                    id,
                                    Some(&identity),
                                    &sample.missing,
                                    &sample.corrupt,
                                )
                                .await?
                            {
                                outcome = "skipped";
                            }
                        } else if sample.inconclusive == 0 && sample.sampled > 0 {
                            outcome = "healthy";
                        }
                    }
                }
            }
            state.cursor = Some(id.0);
            state.checked += 1;
            state.samples += u64::from(sample_count);
            state.processed_this_run += 1;
            match outcome {
                "healthy" => state.healthy += 1,
                "damaged" => state.damaged += 1,
                "skipped" => state.skipped += 1,
                _ => state.unverifiable += 1,
            }
            if outcome != "healthy" {
                state.recent.push(Finding {
                    item: id.0,
                    outcome: outcome.into(),
                    at: now_ms(),
                });
                if state.recent.len() > 50 {
                    state.recent.remove(0);
                }
            }
            if state
                .limit
                .is_some_and(|limit| state.processed_this_run >= limit)
            {
                state.status = Status::Paused;
            }
            self.finish_sweep(view.revision, &mut state).await?;
            Ok(true)
        })
    }
    async fn finish_sweep(&self, revision: i64, state: &mut State) -> Result<(), Error> {
        let _gate = self.sweep_gate.lock().await;
        state.updated_at = now_ms();
        self.db
            .checkpoint_content_sweep(revision, &serde_json::to_string(state)?)
            .await?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_core::{BlobId, FileMeta, ItemKind, NzbFile, Segment, Segments, blob};
    use infinidysk_db::{Db, HealthState, Item, Recheck};
    use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::{LoopbackServer, post};
    use std::sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    };
    use tokio::sync::watch;

    async fn fixture(server: &LoopbackServer) -> (Db, Arc<Client>, Item, Item) {
        let db = Db::connect(&format!(
            "sqlite:{}",
            std::env::temp_dir()
                .join(format!("rust-sweep-{}.sqlite", Uuid::now_v7()))
                .display()
        ))
        .await
        .expect("db");
        let dir = Item::child(
            &db.item(ItemId::CONTENT)
                .await
                .expect("query")
                .expect("root"),
            "cat%",
            ItemKind::Directory,
        )
        .expect("dir");
        let make = |name: &str| {
            let mut item = Item::child(&dir, name, ItemKind::NzbFile).expect("file");
            let id = BlobId::new();
            item.meta_blob = Some(id);
            let meta = FileMeta::Nzb(NzbFile {
                segments: Segments {
                    items: vec![Segment {
                        id: "sweep-0@loopback".into(),
                        size: 16,
                        fallbacks: Vec::new(),
                    }],
                    sizes_trusted: true,
                    ..Segments::default()
                },
                proof: None,
            });
            (item, id, blob::encode_meta(&meta).expect("encode"))
        };
        let (a, aid, adata) = make("a.mkv");
        let (b, bid, bdata) = make("b.mkv");
        db.insert(
            &[dir, a.clone(), b.clone()],
            &[(aid, &adata), (bid, &bdata)],
        )
        .await
        .expect("insert");
        let pool = Pool::new(
            Endpoint::new(
                "127.0.0.1",
                server.addr().port(),
                TlsMode::Plain,
                None,
                Duration::from_secs(3),
            )
            .expect("endpoint"),
            PoolConfig {
                max_connections: 2,
                pipeline_depth: 2,
                min_connections: 0,
                idle_timeout: Duration::from_secs(2),
            },
            Downloads::new(4),
        )
        .expect("pool");
        (
            db,
            Arc::new(Client::new([Provider {
                pool,
                key: "fixture".into(),
                name: "fixture".into(),
                priority: 0,
                backbone: None,
            }])),
            a,
            b,
        )
    }
    fn make_health(db: &Db, client: &Arc<Client>) -> Health {
        Health::new(
            db.clone(),
            Arc::clone(client),
            watch::channel(Vec::new()).1,
            watch::channel(super::super::Settings {
                enabled: false,
                ..Default::default()
            })
            .1,
        )
        .expect("health")
    }
    #[tokio::test]
    async fn sweep_snapshot_survives_restart_limits_and_source_removal() {
        let server = LoopbackServer::start(post("sweep", "fixture.bin", &[7; 16], 16))
            .await
            .expect("server");
        let (db, client, a, b) = fixture(&server).await;
        let health = make_health(&db, &client);
        assert!(
            health
                .control_sweep(
                    0,
                    Control::Start {
                        category: Some("cat%".into()),
                        limit: Some(1)
                    }
                )
                .await
                .expect("start")
        );
        assert_eq!(health.content_sweep().await.expect("state").total, 2);
        assert!(
            !health
                .control_sweep(0, Control::Pause)
                .await
                .expect("stale")
        );
        assert!(health.sweep_step().await.expect("sample"));
        let view = health.content_sweep().await.expect("state");
        assert_eq!(view.state.status, Status::Paused);
        assert_eq!(view.state.checked, 1);
        assert_eq!(view.state.healthy, 1);
        let removed = if view.state.cursor == Some(a.id.0) {
            b.id
        } else {
            a.id
        };
        db.delete_item(removed).await.expect("remove");
        drop(health);
        let restarted = make_health(&db, &client);
        assert!(
            restarted
                .control_sweep(view.revision, Control::Resume)
                .await
                .expect("resume")
        );
        assert!(restarted.sweep_step().await.expect("skip removed"));
        let view = restarted.content_sweep().await.expect("state");
        assert_eq!(view.state.checked, 2);
        assert_eq!(view.state.skipped, 1);
        assert_eq!(view.total, 2);
        restarted
            .control_sweep(view.revision, Control::Resume)
            .await
            .expect("resume");
        restarted.sweep_step().await.expect("complete");
        assert_eq!(
            restarted.content_sweep().await.expect("state").state.status,
            Status::Complete
        );
        let revision = restarted.content_sweep().await.expect("state").revision;
        assert!(
            restarted
                .control_sweep(
                    revision,
                    Control::Start {
                        category: Some("../bad".into()),
                        limit: None
                    }
                )
                .await
                .is_err()
        );
        assert_eq!(
            restarted.content_sweep().await.expect("unchanged").revision,
            revision
        );
    }
    #[tokio::test]
    async fn sweep_distinguishes_missing_from_unavailable_and_protects_new_sources() {
        let server = LoopbackServer::start([]).await.expect("server");
        let (db, client, a, _) = fixture(&server).await;
        let health = make_health(&db, &client);
        health
            .control_sweep(
                0,
                Control::Start {
                    category: None,
                    limit: None,
                },
            )
            .await
            .expect("start");
        health.sweep_step().await.expect("sample");
        let view = health.content_sweep().await.expect("state");
        assert_eq!(view.state.damaged, 1);
        let first = ItemId(view.state.cursor.expect("cursor"));
        assert_eq!(
            db.health(first)
                .await
                .expect("health")
                .expect("file")
                .1
                .state,
            HealthState::Suspect
        );
        let item = db.item(a.id).await.expect("query").expect("item");
        let (_, _, old) = health
            .meta_snapshot(&item)
            .await
            .expect("meta")
            .expect("meta");
        let mut changed = old.clone();
        let FileMeta::Nzb(ref mut f) = changed else {
            panic!("nzb")
        };
        f.segments.items[0].id = "replacement@loopback".into();
        db.update_blob(
            item.meta_blob.expect("blob"),
            &blob::encode_meta(&changed).expect("encode"),
        )
        .await
        .expect("replace");
        assert!(
            !health
                .report_source(
                    item.id,
                    Some(&old.content_identity().expect("identity")),
                    &[0],
                    &[]
                )
                .await
                .expect("stale report")
        );
        let unreachable = Arc::new(Client::new([]));
        let other = make_health(&db, &unreachable);
        let revision = view.revision;
        other
            .control_sweep(
                revision,
                Control::Start {
                    category: None,
                    limit: None,
                },
            )
            .await
            .expect_err("running cannot reset");
        other
            .control_sweep(revision, Control::Pause)
            .await
            .expect("pause");
        let revision = other.content_sweep().await.expect("state").revision;
        other
            .control_sweep(
                revision,
                Control::Start {
                    category: None,
                    limit: None,
                },
            )
            .await
            .expect("new run");
        other.sweep_step().await.expect("unavailable");
        let view = other.content_sweep().await.expect("state");
        assert_eq!(view.state.unverifiable, 1);
        assert_eq!(view.state.damaged, 0);
    }
    #[tokio::test]
    async fn foreground_and_pause_cancel_without_advancing_cursor() {
        let server = LoopbackServer::start_impaired(
            post("sweep", "fixture.bin", &[7; 16], 16),
            infinidysk_testkit::Impairment {
                response_delay: Duration::from_secs(2),
                ..Default::default()
            },
        )
        .await
        .expect("server");
        let (db, client, _, _) = fixture(&server).await;
        let busy = Arc::new(AtomicBool::new(false));
        let gate = Arc::clone(&busy);
        let health = Arc::new(
            make_health(&db, &client).with_gates(|| false, move || gate.load(Ordering::SeqCst)),
        );
        health
            .control_sweep(
                0,
                Control::Start {
                    category: None,
                    limit: None,
                },
            )
            .await
            .expect("start");
        let task = {
            let health = Arc::clone(&health);
            tokio::spawn(async move { health.sweep_step().await })
        };
        tokio::time::sleep(Duration::from_millis(100)).await;
        busy.store(true, Ordering::SeqCst);
        assert!(
            !tokio::time::timeout(Duration::from_millis(500), task)
                .await
                .expect("preempt promptly")
                .expect("join")
                .expect("step")
        );
        assert_eq!(
            health.content_sweep().await.expect("state").state.checked,
            0
        );
        busy.store(false, Ordering::SeqCst);
        let task = {
            let health = Arc::clone(&health);
            tokio::spawn(async move { health.sweep_step().await })
        };
        tokio::time::sleep(Duration::from_millis(50)).await;
        let revision = health.content_sweep().await.expect("state").revision;
        health
            .control_sweep(revision, Control::Pause)
            .await
            .expect("pause");
        task.await.expect("join").expect("cancel");
        let view = health.content_sweep().await.expect("state");
        assert_eq!(view.state.status, Status::Paused);
        assert_eq!(view.state.checked, 0);
    }
    #[tokio::test]
    async fn priority_checks_precede_analysis_but_do_not_starve_sweeps() {
        let server = LoopbackServer::start(post("sweep", "fixture.bin", &[7; 16], 16))
            .await
            .expect("server");
        let (db, client, a, _) = fixture(&server).await;
        let analyzed = Arc::new(AtomicU64::new(0));
        let count = Arc::clone(&analyzed);
        let health = make_health(&db, &client).with_analyzer(Arc::new(move |_| {
            let count = Arc::clone(&count);
            Box::pin(async move {
                count.fetch_add(1, Ordering::SeqCst);
                super::super::Analysis::Unsupported("fixture".into())
            })
        }));
        health
            .control_sweep(
                0,
                Control::Start {
                    category: None,
                    limit: None,
                },
            )
            .await
            .expect("start");
        db.request_media_probe(a.id, now_ms()).await.expect("probe");
        for _ in 0..4 {
            db.request_recheck(a.id, Recheck::User)
                .await
                .expect("request");
            health.step().await.expect("check");
        }
        assert_eq!(analyzed.load(Ordering::SeqCst), 0);
        assert_eq!(
            health.content_sweep().await.expect("state").state.checked,
            0
        );
        db.request_recheck(a.id, Recheck::User)
            .await
            .expect("request");
        health.step().await.expect("maintenance");
        assert_eq!(
            health.content_sweep().await.expect("state").state.checked,
            1
        );
    }
    #[tokio::test]
    async fn sampled_probe_budget_and_known_holes_are_bounded() {
        let articles = post("budget", "fixture.bin", &vec![7; 20_000], 10);
        let server = LoopbackServer::start(articles.clone())
            .await
            .expect("server");
        let (_, client, _, _) = fixture(&server).await;
        let mut meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: articles
                    .iter()
                    .map(|a| Segment {
                        id: a.message_id.clone(),
                        size: 10,
                        fallbacks: Vec::new(),
                    })
                    .collect(),
                sizes_trusted: true,
                ..Segments::default()
            },
            proof: None,
        });
        let sample = check::import_sample(&client, &meta).await;
        assert_eq!(sample.sampled, 20);
        assert_eq!(sample.inconclusive, 0);
        assert!(sample.missing.is_empty() && sample.corrupt.is_empty());
        let FileMeta::Nzb(ref mut file) = meta else {
            panic!("nzb")
        };
        file.segments.missing = (0..200).collect();
        let sample = check::import_sample(&client, &meta).await;
        assert_eq!(sample.sampled, 64);
        assert_eq!(sample.inconclusive, 0);
    }
    #[tokio::test]
    async fn a_running_check_cannot_erase_a_new_playback_fault() {
        let server = LoopbackServer::start_stalling(
            post("sweep", "fixture.bin", &[7; 16], 16),
            0,
            Duration::from_millis(350),
        )
        .await
        .expect("server");
        let (db, client, a, _) = fixture(&server).await;
        let health = Arc::new(make_health(&db, &client));
        db.request_recheck(a.id, Recheck::User)
            .await
            .expect("request");
        let task = {
            let health = Arc::clone(&health);
            tokio::spawn(async move { health.step().await })
        };
        tokio::time::sleep(Duration::from_millis(100)).await;
        health
            .report(a.id, &[0], &[])
            .await
            .expect("playback fault");
        task.await.expect("join").expect("check");
        let (_, _, meta) = health
            .meta_snapshot(&a)
            .await
            .expect("meta")
            .expect("source");
        let FileMeta::Nzb(file) = meta else {
            panic!("nzb")
        };
        assert_eq!(file.segments.missing, [0]);
        let (_, state) = db.health(a.id).await.expect("health").expect("source");
        assert_eq!(state.state, HealthState::Suspect);
        assert_eq!(state.recheck, Recheck::Playback);
    }
}
