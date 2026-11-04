//! Durable automatic local repair against real SQLite, NNTP and the shared VFS.
use infinidysk::repair::worker::{Settings, Worker};
use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, NzbFile, Par2Proof, Segment, Segments, blob,
};
use infinidysk_db::{Db, HealthState, Item, Recheck};
use infinidysk_health::{Health, Settings as HealthSettings, UnownedAction};
use infinidysk_native_cache::repair::{Error as StoreError, Revision, Store};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_par2::repair::RecoverySet;
use infinidysk_stream::{Budget, Cache};
use infinidysk_testkit::{LoopbackServer, post};
use infinidysk_vfs::{Delivery, Origin, Vfs};
use std::fmt::Write as _;
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt as _, AsyncSeekExt as _},
    sync::watch,
};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;
struct Fixture {
    path: PathBuf,
    db: Db,
    item: Item,
    meta: FileMeta,
    store: Arc<Store>,
    client: Arc<Client>,
    vfs: Arc<Vfs>,
    worker: Arc<Worker>,
    health: Arc<Health>,
    server: LoopbackServer,
    settings: Settings,
}
fn data(n: usize) -> Vec<u8> {
    (0..n)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect()
}
fn file(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/../par2/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture")
}
async fn setup(settings: Settings, missing: bool, witness: Option<Arc<AtomicBool>>) -> Fixture {
    let path = std::env::temp_dir().join(format!("infinidysk-par2-worker-{}", Uuid::new_v4()));
    std::fs::create_dir(&path).expect("directory");
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("db");
    let set = RecoverySet::read(&file("set.par2"), None).expect("set");
    let mut articles = Vec::new();
    let mut posted = Vec::new();
    let mut target = None;
    for (index, desc) in set.files().iter().enumerate() {
        let bytes = data(usize::try_from(desc.length).expect("length"));
        let pieces = post(
            &format!("worker-source-{index}"),
            "fixture.bin",
            &bytes,
            3000,
        );
        let segments = Segments {
            items: pieces
                .iter()
                .enumerate()
                .map(|(i, part)| Segment {
                    id: part.message_id.clone(),
                    size: u32::try_from(3000.min(bytes.len() - i * 3000)).expect("size"),
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            missing: vec![1],
            corrupt: vec![],
        };
        if index == 0 {
            target = Some(FileMeta::Nzb(NzbFile {
                segments,
                proof: Some(Par2Proof {
                    file_length: desc.length,
                    file_md5: desc.hash,
                    md5_16k: desc.hash_16k,
                    file_id: desc.id,
                    slice_size: u32::try_from(set.slice_size()).expect("slice size"),
                    slice_md5: desc.slices.iter().map(|slice| slice.md5).collect(),
                    slice_crc32: desc.slices.iter().map(|slice| slice.crc32).collect(),
                }),
            }));
        }
        posted.push(
            pieces
                .iter()
                .map(|part| part.message_id.clone())
                .collect::<Vec<_>>(),
        );
        articles.extend(
            pieces
                .into_iter()
                .enumerate()
                .filter(|(i, _)| !missing || index != 0 || *i != 1)
                .map(|(_, part)| part),
        );
    }
    let recovery = post(
        "worker-recovery",
        "fixture.bin",
        &file("set.vol0+3.par2"),
        113,
    );
    posted.push(
        recovery
            .iter()
            .map(|part| part.message_id.clone())
            .collect(),
    );
    articles.extend(recovery);
    let mut xml = String::from("<nzb>");
    for (index, ids) in posted.iter().enumerate() {
        write!(
            xml,
            "<file poster=\"fixture\" date=\"0\" subject=\"fixture-{index}.bin\"><segments>"
        )
        .expect("XML");
        for (part, id) in ids.iter().enumerate() {
            write!(
                xml,
                "<segment bytes=\"4000\" number=\"{}\">{id}</segment>",
                part + 1
            )
            .expect("XML");
        }
        xml.push_str("</segments></file>");
    }
    xml.push_str("</nzb>");
    let root = db
        .item(ItemId::CONTENT)
        .await
        .expect("root")
        .expect("content");
    let mut item = Item::child(&root, "fixture.mkv", ItemKind::NzbFile).expect("item");
    let meta = target.expect("target");
    let meta_id = BlobId::new();
    let nzb_id = BlobId::new();
    item.meta_blob = Some(meta_id);
    item.nzb_blob = Some(nzb_id);
    item.size = Some(meta.size());
    db.insert(
        std::slice::from_ref(&item),
        &[
            (meta_id, &blob::encode_meta(&meta).expect("metadata")),
            (nzb_id, &blob::encode_nzb(xml.as_bytes()).expect("nzb")),
        ],
    )
    .await
    .expect("insert");
    let server = LoopbackServer::start(articles).await.expect("server");
    let downloads = Downloads::new(8);
    let pool = Pool::new(
        Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .expect("endpoint"),
        PoolConfig {
            max_connections: 2,
            min_connections: 0,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
        },
        downloads,
    )
    .expect("pool");
    let client = Arc::new(Client::new([Provider {
        pool,
        key: "fixture".into(),
        name: "fixture".into(),
        priority: 0,
        backbone: None,
    }]));
    let store = Store::open(&path.join("repair")).await.expect("store");
    assert!(client.set_repairs(store.clone()));
    if let Some(fail) = witness {
        assert!(store.set_witness(Arc::new(move |_, _| {
            let fail = fail.clone();
            Box::pin(async move {
                if fail.load(Ordering::Acquire) {
                    Err(StoreError::Witness)
                } else {
                    Ok(())
                }
            })
        })));
    }
    let cache = Arc::new(Cache::new(true, 4 << 20));
    client.set_sink(Box::new({
        let cache = cache.clone();
        move |id, _, body, _, stamp| cache.insert_revision(id, stamp, body.data.clone())
    }));
    let vfs = Arc::new(
        Vfs::new(
            db.clone(),
            client.clone(),
            Budget::new(4 << 20),
            0,
            "/unused-fixture-mount",
        )
        .with_cache(cache),
    );
    let worker = Worker::load(db.clone(), client.clone(), vfs.clone(), store.clone())
        .await
        .expect("worker");
    let health = Arc::new(
        Health::new(
            db.clone(),
            client.clone(),
            watch::channel(vec![]).1,
            watch::channel(HealthSettings {
                media_probe: false,
                unowned_action: UnownedAction::ActionNeeded,
                ..HealthSettings::default()
            })
            .1,
        )
        .expect("health")
        .with_in_use({
            let vfs = vfs.clone();
            move |id| vfs.sessions().open().iter().any(|s| s.item == id)
        })
        .with_local_repair(Arc::new({
            let worker = Arc::downgrade(&worker);
            move |item, _| {
                let worker = worker.clone();
                Box::pin(async move {
                    let Some(worker) = worker.upgrade() else {
                        return Ok(false);
                    };
                    worker.offer(item.id, settings).await.map_err(|_| {
                        infinidysk_health::Error::Regrab("local repair queue unavailable")
                    })
                })
            }
        })),
    );
    assert!(worker.bind_health(&health));
    assert!(store.set_mutation_gate(health.mutation_gate()));
    Fixture {
        path,
        db,
        item,
        meta,
        store,
        client,
        vfs,
        worker,
        health,
        server,
        settings,
    }
}
impl Fixture {
    fn cleanup(self) {
        let path = self.path.clone();
        drop(self);
        std::fs::remove_dir_all(path).expect("cleanup");
    }
}
#[tokio::test]
async fn playback_damage_is_repaired_and_verified_before_replacement_and_seeks_use_shared_bytes() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("playback fault");
    let queued = fixture.worker.status().await.expect("queued");
    assert_eq!(queued.len(), 1);
    assert!(queued[0].urgent);
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("repair")
    );
    let jobs = fixture.worker.status().await.expect("outcome");
    assert_eq!(jobs[0].state, "repaired");
    assert_eq!(jobs[0].patch_articles, 3);
    assert!(
        fixture
            .db
            .item(fixture.item.id)
            .await
            .expect("source")
            .is_some()
    );
    let (_, status) = fixture
        .db
        .health(fixture.item.id)
        .await
        .expect("health")
        .expect("file");
    assert_eq!(status.state, HealthState::Unchecked);
    assert_eq!(status.recheck, Recheck::Playback);
    assert!(fixture.health.step().await.expect("fresh verification"));
    assert_eq!(
        fixture
            .db
            .health(fixture.item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    fixture.client.replace([]);
    let before = fixture.server.body_commands();
    let node = fixture
        .vfs
        .lookup(&fixture.item.path)
        .await
        .expect("lookup")
        .expect("node");
    let mut reader = fixture
        .vfs
        .open(
            &node,
            Origin {
                delivery: Delivery::WebDav,
                client: "fixture".into(),
                player: None,
            },
        )
        .await
        .expect("open")
        .expect("reader");
    let expected = data(usize::try_from(fixture.meta.size()).expect("size"));
    let mut full = Vec::new();
    reader
        .read_to_end(&mut full)
        .await
        .expect("provider-free shared read");
    assert_eq!(full, expected);
    reader
        .seek(std::io::SeekFrom::Start(3000))
        .await
        .expect("seek");
    let mut bytes = vec![0; 3000];
    reader.read_exact(&mut bytes).await.expect("seek bytes");
    assert_eq!(bytes, expected[3000..6000]);
    assert_eq!(fixture.server.body_commands(), before);
    drop(reader);
    fixture.cleanup();
}
#[tokio::test]
async fn readers_defer_before_provider_work_and_last_close_admits_the_urgent_attempt() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    let node = fixture
        .vfs
        .lookup(&fixture.item.path)
        .await
        .expect("lookup")
        .expect("node");
    let reader = fixture
        .vfs
        .open(
            &node,
            Origin {
                delivery: Delivery::WebDav,
                client: "fixture".into(),
                player: None,
            },
        )
        .await
        .expect("open")
        .expect("reader");
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("defer")
    );
    assert_eq!(fixture.server.body_commands(), 0);
    assert_eq!(
        fixture.worker.status().await.expect("status")[0].state,
        "deferred"
    );
    drop(reader);
    fixture
        .health
        .readers_closed(fixture.item.id)
        .await
        .expect("last-close wakeup");
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("repair after last close")
    );
    assert_eq!(
        fixture.worker.status().await.expect("status")[0].state,
        "repaired"
    );
    fixture.cleanup();
}
#[tokio::test]
async fn provider_outages_keep_urgent_attempts_deferred_without_replacing_the_source() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    fixture.client.replace([]);
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("defer")
    );
    let jobs = fixture.worker.status().await.expect("status");
    assert_eq!(jobs[0].state, "deferred");
    assert!(jobs[0].urgent);
    assert!(jobs[0].next_attempt <= infinidysk_db::now_ms() + 301_000);
    assert!(
        fixture
            .db
            .item(fixture.item.id)
            .await
            .expect("source retained")
            .is_some()
    );
    assert_eq!(fixture.store.epoch(), 0);
    fixture.cleanup();
}
#[tokio::test]
async fn healthy_original_posts_commit_with_a_fresh_check_without_manufacturing_patch_revisions() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        false,
        None,
    )
    .await;
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("stale fault");
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("healthy source")
    );
    assert_eq!(
        fixture.worker.status().await.expect("status")[0].state,
        "healthy"
    );
    assert_eq!(fixture.store.epoch(), 0);
    assert_eq!(
        fixture.store.revision("worker-source-0-1@loopback"),
        Revision::Base
    );
    assert_eq!(
        fixture
            .db
            .health(fixture.item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Unchecked
    );
    assert!(fixture.health.step().await.expect("fresh source check"));
    assert_eq!(
        fixture
            .db
            .health(fixture.item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    fixture.cleanup();
}
#[tokio::test]
async fn uncertain_publication_is_read_back_after_worker_restart_without_more_provider_reads() {
    let fail = Arc::new(AtomicBool::new(true));
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        Some(fail.clone()),
    )
    .await;
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("uncertain publication")
    );
    assert!(fixture.store.is_pending());
    let jobs = fixture.worker.status().await.expect("status");
    assert_eq!(jobs[0].state, "publishing");
    let before = fixture.server.body_commands();
    fail.store(false, Ordering::Release);
    let restarted = Worker::load(
        fixture.db.clone(),
        fixture.client.clone(),
        fixture.vfs.clone(),
        fixture.store.clone(),
    )
    .await
    .expect("restart");
    assert!(restarted.bind_health(&fixture.health));
    let row = fixture
        .db
        .repair_attempts(128)
        .await
        .expect("journal")
        .into_iter()
        .find(|row| row.id == jobs[0].id)
        .expect("intent");
    assert!(
        fixture
            .db
            .update_repair_attempt(
                &row,
                "publishing",
                row.priority,
                0,
                infinidysk_db::now_ms(),
                &row.payload
            )
            .await
            .expect("readback due")
    );
    assert!(
        restarted
            .tick(Settings::default(), CancellationToken::new())
            .await
            .expect("readback even with new attempts off")
    );
    assert_eq!(
        restarted.status().await.expect("status")[0].state,
        "repaired"
    );
    assert!(!fixture.store.is_pending());
    assert_eq!(
        fixture.server.body_commands(),
        before,
        "accepted/uncertain writes cannot replay discovery or publication"
    );
    drop(restarted);
    fixture.cleanup();
}

#[tokio::test]
async fn changed_source_waiting_for_publication_cannot_install_stale_patches() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    let gate = fixture.health.mutation_gate();
    let held = gate.write().await;
    let task = {
        let worker = fixture.worker.clone();
        let settings = fixture.settings;
        tokio::spawn(async move { worker.tick(settings, CancellationToken::new()).await })
    };
    tokio::time::timeout(Duration::from_secs(10), async {
        loop {
            if fixture.worker.status().await.expect("status")[0].state == "publishing" {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("publication queued behind reader barrier");
    let mut changed = fixture.meta.clone();
    let FileMeta::Nzb(file) = &mut changed else {
        panic!("NZB fixture")
    };
    file.segments.items[0].id = "different-source-article".into();
    fixture
        .db
        .update_blob(
            fixture.item.meta_blob.expect("metadata"),
            &blob::encode_meta(&changed).expect("encode"),
        )
        .await
        .expect("source changed");
    drop(held);
    task.await.expect("worker").expect("publication readback");
    assert_eq!(
        fixture.store.epoch(),
        0,
        "old verified input cannot publish after source replacement"
    );
    assert!(!fixture.store.is_pending());
    assert_ne!(
        fixture.worker.status().await.expect("status")[0].state,
        "repaired"
    );
    fixture.cleanup();
}
#[tokio::test]
async fn reconstruction_limit_is_terminal_for_the_same_damage_and_policy_without_provider_replay() {
    let fixture = setup(
        Settings {
            enabled: true,
            max_missing_slices: 1,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    fixture
        .worker
        .tick(fixture.settings, CancellationToken::new())
        .await
        .expect("bounded attempt");
    let rows = fixture.worker.status().await.expect("status");
    assert_eq!(rows[0].state, "limit");
    assert_eq!(rows[0].attempts, 1);
    assert_eq!(fixture.store.epoch(), 0);
    let before = fixture.server.body_commands();
    assert!(
        !fixture
            .worker
            .offer(fixture.item.id, fixture.settings)
            .await
            .expect("cooldown permits normal confirmed-damage policy")
    );
    assert!(
        !fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("no repeat")
    );
    assert_eq!(fixture.server.body_commands(), before);
    fixture.cleanup();
}
#[tokio::test]
async fn foreground_import_demand_prevents_new_repair_io_and_preserves_urgent_intent() {
    let fixture = setup(
        Settings {
            enabled: true,
            ..Settings::default()
        },
        true,
        None,
    )
    .await;
    let busy = Arc::new(AtomicBool::new(true));
    assert!(fixture.worker.set_busy(Arc::new({
        let busy = busy.clone();
        move || busy.load(Ordering::Acquire)
    })));
    fixture
        .health
        .report(fixture.item.id, &[1], &[])
        .await
        .expect("fault");
    assert!(
        !fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("yield")
    );
    assert_eq!(fixture.server.body_commands(), 0);
    assert!(fixture.worker.status().await.expect("status")[0].urgent);
    busy.store(false, Ordering::Release);
    assert!(
        fixture
            .worker
            .tick(fixture.settings, CancellationToken::new())
            .await
            .expect("resume")
    );
    assert_eq!(
        fixture.worker.status().await.expect("status")[0].state,
        "repaired"
    );
    fixture.cleanup();
}
