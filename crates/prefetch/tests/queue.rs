#![allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, Item};
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, FolderSettings, Settings};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_prefetch::{Operation, Queue, Range, Settings as WarmSettings, State};
use infinidysk_stream::Budget;
use infinidysk_testkit::{Corpus, LoopbackServer};
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use std::io::SeekFrom;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use uuid::Uuid;

struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let p = std::env::temp_dir().join(format!("id-native-vfs-{}", ItemId::new()));
        std::fs::create_dir_all(p.join("cache")).unwrap();
        Self(p)
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
async fn fixture(
    t: &Temp,
    missing: bool,
) -> (
    Arc<Vfs>,
    Item,
    Arc<Cache>,
    Corpus,
    LoopbackServer,
    Arc<Client>,
) {
    fixture_with(t, missing, 0).await
}
async fn fixture_with(
    t: &Temp,
    missing: bool,
    stall: u8,
) -> (
    Arc<Vfs>,
    Item,
    Arc<Cache>,
    Corpus,
    LoopbackServer,
    Arc<Client>,
) {
    let corpus = Corpus::generate(1025, 32, 256 * 1024);
    let articles = corpus
        .articles
        .iter()
        .enumerate()
        .filter(|(i, _)| !missing || *i != 4)
        .map(|(_, a)| a.clone())
        .collect::<Vec<_>>();
    let server = if stall == 1 {
        LoopbackServer::start_stalling(articles, 0, Duration::from_secs(2))
            .await
            .unwrap()
    } else if stall == 2 {
        LoopbackServer::start_impaired(
            articles,
            infinidysk_testkit::Impairment {
                response_delay: Duration::from_millis(500),
                ..Default::default()
            },
        )
        .await
        .unwrap()
    } else {
        LoopbackServer::start(articles).await.unwrap()
    };
    let db = Db::connect(&format!("sqlite:{}", t.0.join("data.sqlite").display()))
        .await
        .unwrap();
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let category = Item::child(&root, "video", ItemKind::Directory).unwrap();
    let mut item = Item::child(&category, "file.mkv", ItemKind::NzbFile).unwrap();
    let mut segs = Segments {
        items: corpus
            .articles
            .iter()
            .map(|a| Segment {
                id: a.message_id.clone(),
                size: 256 * 1024,
                fallbacks: vec![],
            })
            .collect(),
        sizes_trusted: true,
        ..Segments::default()
    };
    if missing {
        segs.missing = vec![4];
    }
    let meta = FileMeta::Nzb(NzbFile {
        segments: segs,
        proof: None,
    });
    let id = BlobId::new();
    item.meta_blob = Some(id);
    item.size = Some(2 * BLOCK);
    db.insert(
        &[category, item.clone()],
        &[(id, &blob::encode_meta(&meta).unwrap())],
    )
    .await
    .unwrap();
    let pool = Pool::new(
        Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(2),
        )
        .unwrap(),
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        },
        Downloads::new(16),
    )
    .unwrap();
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: "loopback".to_owned(),
        priority: 0,
        backbone: None,
    }]));
    let cache = Cache::open(Settings {
        enabled: true,
        min_file_mb: 0,
        metadata_path: t.0.join("metadata").to_str().unwrap().to_owned(),
        folders: vec![FolderSettings {
            id: uuid::Uuid::from_u128(1),
            path: t.0.join("cache").to_str().unwrap().to_owned(),
            min_free_bytes: 0,
            ..FolderSettings::default()
        }],
        ..Settings::default()
    })
    .await
    .unwrap();
    let vfs = Vfs::new(
        db,
        Arc::clone(&client),
        Budget::new(8 * 1024 * 1024),
        1024 * 1024,
        "/unused-test-mount",
    )
    .with_native_cache(Some(Arc::clone(&cache)));
    (Arc::new(vfs), item, cache, corpus, server, client)
}
fn settings() -> WarmSettings {
    WarmSettings {
        enabled: true,
        daily_fill_bytes: 100 * BLOCK,
        ..Default::default()
    }
}
async fn enqueue(queue: &Queue, item: &Item) -> infinidysk_prefetch::Job {
    queue
        .enqueue(
            item.id,
            &[Range {
                start: 0,
                end: item.size.unwrap(),
            }],
            50,
            Uuid::new_v4(),
        )
        .await
        .unwrap()
}
#[tokio::test]
async fn durable_queue_warms_shared_cache_and_seeks_use_no_provider() {
    let t = Temp::new();
    let (vfs, item, cache, corpus, server, client) = fixture(&t, false).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    let job = enqueue(&q, &item).await;
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(q.status().await.jobs[0].verified_bytes, BLOCK);
    assert!(
        vfs.sessions().open().is_empty(),
        "warming cannot masquerade as a viewer"
    );
    drop(q);
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    assert_eq!(
        q.status().await.jobs[0].verified_bytes,
        0,
        "restart revalidates coverage"
    );
    let before = server.body_commands();
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(
        server.body_commands(),
        before,
        "retained block survives restart"
    );
    assert!(q.tick(&settings()).await.unwrap());
    let status = q.status().await;
    assert_eq!(status.jobs[0].state, State::Complete);
    assert_eq!(status.jobs[0].id, job.id);
    assert_eq!(status.jobs[0].source_bytes, 2 * BLOCK);
    assert!(
        cache
            .verify_complete(&vfs.cache_key(&item).await.unwrap())
            .await
            .unwrap()
            .unwrap()
    );
    let before = server.body_commands();
    for delivery in [Delivery::WebDav, Delivery::Fuse] {
        let mut read = vfs
            .open(
                &Node::Item(item.clone()),
                Origin {
                    delivery,
                    client: "test".into(),
                    player: None,
                },
            )
            .await
            .unwrap()
            .unwrap();
        read.seek(SeekFrom::Start(BLOCK - 19)).await.unwrap();
        let mut b = [0; 67];
        read.read_exact(&mut b).await.unwrap();
        assert_eq!(b, corpus.data[BLOCK_BYTES - 19..BLOCK_BYTES + 48]);
    }
    assert_eq!(server.body_commands(), before);
    let stats = client.stats();
    assert!(stats[0].workloads[infinidysk_nntp::Workload::Warm as usize].bytes >= 2 * BLOCK);
    assert_eq!(
        stats[0].workloads[infinidysk_nntp::Workload::Health as usize].bytes,
        0
    );
}
#[tokio::test]
async fn pause_cancel_daily_allowance_and_idempotence_are_durable() {
    let t = Temp::new();
    let (vfs, item, _, _, server, _) = fixture(&t, false).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    let job = enqueue(&q, &item).await;
    let duplicate = q
        .enqueue(item.id, &job.ranges, 50, job.intent)
        .await
        .unwrap();
    assert_eq!(duplicate.id, job.id);
    q.pause(true).await.unwrap();
    assert!(!q.tick(&settings()).await.unwrap());
    assert_eq!(server.body_commands(), 0);
    drop(q);
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    assert!(q.status().await.paused);
    q.pause(false).await.unwrap();
    q.control(job.id, Operation::Pause).await.unwrap();
    assert!(!q.tick(&settings()).await.unwrap());
    q.control(job.id, Operation::Resume).await.unwrap();
    let limited = WarmSettings {
        enabled: true,
        daily_fill_bytes: BLOCK,
        ..Default::default()
    };
    assert!(q.tick(&limited).await.unwrap());
    let before = server.body_commands();
    assert!(!q.tick(&limited).await.unwrap());
    assert_eq!(server.body_commands(), before);
    q.control(job.id, Operation::Cancel).await.unwrap();
    assert!(!q.tick(&settings()).await.unwrap());
    drop(q);
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    assert_eq!(q.status().await.jobs[0].state, State::Cancelled);
}
#[tokio::test]
async fn failed_source_read_requests_urgent_health_without_publishing_filler() {
    let t = Temp::new();
    let (vfs, item, cache, _, _server, _) = fixture(&t, true).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    enqueue(&q, &item).await;
    assert!(q.tick(&settings()).await.unwrap());
    let job = &q.status().await.jobs[0];
    assert_eq!(job.attempts, 1);
    assert_eq!(job.verified_bytes, 0);
    assert!(job.next_attempt > infinidysk_db::now_ms());
    assert!(vfs.db().health_is_urgent(item.id).await.unwrap());
    assert_eq!(cache.stats().await.unwrap().bytes, 0);
    assert!(
        !q.tick(&settings()).await.unwrap(),
        "retry backoff must survive normal polling"
    );
}
#[tokio::test]
async fn changed_source_supersedes_job_without_downloading_old_revision() {
    let t = Temp::new();
    let (vfs, item, _, _, server, _) = fixture(&t, false).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    enqueue(&q, &item).await;
    let raw = vfs
        .db()
        .blob(item.meta_blob.unwrap())
        .await
        .unwrap()
        .unwrap();
    let mut meta = blob::decode_meta(&raw).unwrap();
    if let FileMeta::Nzb(n) = &mut meta {
        n.segments.items[0].id = "replacement@test.invalid".into();
    }
    vfs.db()
        .update_blob(item.meta_blob.unwrap(), &blob::encode_meta(&meta).unwrap())
        .await
        .unwrap();
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(q.status().await.jobs[0].state, State::Superseded);
    assert_eq!(server.body_commands(), 0);
}
#[tokio::test]
async fn cancellation_interrupts_stalled_source_without_publishing_a_partial_block() {
    let t = Temp::new();
    let (vfs, item, cache, _, server, _) = fixture_with(&t, false, 1).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    let job = enqueue(&q, &item).await;
    let running = {
        let q = Arc::clone(&q);
        tokio::spawn(async move { q.tick(&settings()).await })
    };
    tokio::time::timeout(Duration::from_secs(2), async {
        while server.body_commands() == 0 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    q.control(job.id, Operation::Cancel).await.unwrap();
    assert!(
        !tokio::time::timeout(Duration::from_secs(1), running)
            .await
            .unwrap()
            .unwrap()
            .unwrap()
    );
    assert_eq!(cache.stats().await.unwrap().bytes, 0);
    assert_eq!(q.status().await.jobs[0].state, State::Cancelled);
    assert_eq!(
        q.status().await.charged_fill_bytes,
        BLOCK,
        "interrupted attempts retain their allowance charge"
    );
}
#[tokio::test]
async fn database_failure_never_acknowledges_a_control_change() {
    let t = Temp::new();
    let (vfs, item, _, _, _, _) = fixture(&t, false).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    let job = enqueue(&q, &item).await;
    vfs.db().close().await;
    assert!(q.control(job.id, Operation::Cancel).await.is_err());
    assert_eq!(q.status().await.jobs[0].state, State::Queued);
}
#[tokio::test]
async fn foreground_wait_interrupts_warming_but_idle_open_does_not_block_it() {
    let t = Temp::new();
    let (vfs, item, cache, _, server, _) = fixture_with(&t, false, 2).await;
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    enqueue(&q, &item).await;
    let running = {
        let q = Arc::clone(&q);
        tokio::spawn(async move { q.tick(&settings()).await })
    };
    tokio::time::timeout(Duration::from_secs(2), async {
        while server.body_commands() == 0 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    let mut foreground = vfs
        .open(
            &Node::Item(item.clone()),
            Origin {
                delivery: Delivery::WebDav,
                client: "test".into(),
                player: None,
            },
        )
        .await
        .unwrap()
        .unwrap();
    foreground.seek(SeekFrom::Start(BLOCK)).await.unwrap();
    let playback = tokio::spawn(async move {
        let mut b = [0; 64];
        foreground.read_exact(&mut b).await.unwrap();
    });
    assert!(
        !tokio::time::timeout(Duration::from_secs(1), running)
            .await
            .unwrap()
            .unwrap()
            .unwrap()
    );
    assert_eq!(cache.stats().await.unwrap().bytes, 0);
    tokio::time::timeout(Duration::from_secs(3), playback)
        .await
        .unwrap()
        .unwrap();
    let _idle = vfs
        .open(
            &Node::Item(item.clone()),
            Origin {
                delivery: Delivery::Fuse,
                client: "idle".into(),
                player: None,
            },
        )
        .await
        .unwrap()
        .unwrap();
    assert!(
        tokio::time::timeout(Duration::from_secs(15), q.tick(&settings()))
            .await
            .unwrap()
            .unwrap(),
        "idle playback handles consume no warming allowance or connections"
    );
}

async fn member_warming_case(encrypted: bool) {
    let t = Temp::new();
    let (vfs, plain, cache, corpus, server, _) = fixture(&t, false).await;
    let parent = vfs.db().item(plain.parent.unwrap()).await.unwrap().unwrap();
    let FileMeta::Nzb(source) = blob::decode_meta(
        &vfs.db()
            .blob(plain.meta_blob.unwrap())
            .await
            .unwrap()
            .unwrap(),
    )
    .unwrap() else {
        unreachable!()
    };
    let (range, crypt, expected) = if encrypted {
        let crypt = infinidysk_core::crypt::Crypt {
            key: infinidysk_core::crypt::Key::Aes256([9; 32]),
            iv: [3; 16],
            size: corpus.data.len() as u64 - 7,
        };
        let mut expected = corpus.data.clone();
        infinidysk_core::crypt::Cipher::new(&crypt.key)
            .decrypt(&mut crypt.iv.clone(), &mut expected);
        expected.truncate(expected.len() - 7);
        (
            infinidysk_core::ByteRange {
                start: 0,
                end: corpus.data.len() as u64,
            },
            Some(crypt),
            expected,
        )
    } else {
        (
            infinidysk_core::ByteRange {
                start: 37,
                end: corpus.data.len() as u64 - 5,
            },
            None,
            corpus.data[37..corpus.data.len() - 5].to_vec(),
        )
    };
    let meta = FileMeta::Multipart(infinidysk_core::MultipartFile {
        parts: vec![infinidysk_core::FilePart {
            segments: source.segments,
            offset: 0,
            data: range,
            split_after: false,
            proof: None,
        }],
        crypt,
    });
    let mut item = Item::child(&parent, "member.bin", ItemKind::Multipart).unwrap();
    let id = BlobId::new();
    item.meta_blob = Some(id);
    item.size = Some(meta.size());
    vfs.db()
        .insert(
            std::slice::from_ref(&item),
            &[(id, &blob::encode_meta(&meta).unwrap())],
        )
        .await
        .unwrap();
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    enqueue(&q, &item).await;
    assert!(q.tick(&settings()).await.unwrap());
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(q.status().await.jobs[0].state, State::Complete);
    assert_eq!(cache.stats().await.unwrap().bytes, expected.len() as u64);
    let node = Node::Item(item);
    let before = server.body_commands();
    let mut cached = vfs
        .open(
            &node,
            Origin {
                delivery: Delivery::Fuse,
                client: "fixture".into(),
                player: None,
            },
        )
        .await
        .unwrap()
        .unwrap();
    for offset in [17, expected.len() - 512, 0] {
        cached.seek(SeekFrom::Start(offset as u64)).await.unwrap();
        let mut bytes = [0; 512];
        cached.read_exact(&mut bytes).await.unwrap();
        assert_eq!(bytes, expected[offset..offset + 512]);
    }
    assert_eq!(server.body_commands(), before);
}

#[tokio::test]
async fn stored_member_warming_uses_final_media_offsets() {
    member_warming_case(false).await;
}
#[tokio::test]
async fn encrypted_member_warming_publishes_plaintext_and_unpadded_tail() {
    member_warming_case(true).await;
}

#[tokio::test]
async fn warming_reports_the_exact_confirmed_missing_slice_for_health_repair() {
    let t = Temp::new();
    let (original, item, cache, _, _server, client) = fixture(&t, true).await;
    let faults = Arc::new(std::sync::Mutex::new(Vec::new()));
    let reported = Arc::clone(&faults);
    let vfs = Arc::new(
        Vfs::new(
            original.db().clone(),
            client,
            Budget::new(8 * 1024 * 1024),
            0,
            "/unused",
        )
        .with_native_cache(Some(cache))
        .with_report(Arc::new(move |id, index, fault| {
            reported.lock().unwrap().push((id, index, fault));
        })),
    );
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    enqueue(&q, &item).await;
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(
        *faults.lock().unwrap(),
        vec![(item.id, 4, infinidysk_stream::Fault::Missing)]
    );
    assert!(vfs.db().health_is_urgent(item.id).await.unwrap());
}
