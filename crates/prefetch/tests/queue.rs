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
    fixture_sized(t, missing, stall, 32).await
}
async fn fixture_sized(
    t: &Temp,
    missing: bool,
    stall: u8,
    articles: usize,
) -> (
    Arc<Vfs>,
    Item,
    Arc<Cache>,
    Corpus,
    LoopbackServer,
    Arc<Client>,
) {
    let corpus = Corpus::generate(1025, articles, 256 * 1024);
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
    item.size = Some(corpus.data.len() as u64);
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
    let (vfs, item, _cache, _corpus, _server, _client) = fixture(&t, false).await;
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

#[tokio::test]
async fn wire_quota_is_durable_never_marks_source_damaged_and_does_not_charge_cache_hits() {
    let t = Temp::new();
    let (vfs, item, cache, _, server, _) = fixture(&t, false).await;
    let queue = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    let job = enqueue(&queue, &item).await;
    let mut policy = settings();
    policy.daily_wire_bytes = BLOCK;
    assert!(!queue.tick(&policy).await.unwrap());
    let status = queue.status().await;
    assert_eq!(status.reserved_wire_bytes, Some(BLOCK));
    assert_eq!(status.received_wire_bytes, BLOCK);
    assert!(
        status.jobs[0]
            .reason
            .as_deref()
            .unwrap()
            .contains("Wire allowance")
    );
    assert_eq!(status.jobs[0].state, State::Queued);
    assert!(
        cache
            .coverage(&vfs.cache_key(&item).await.unwrap())
            .await
            .unwrap()
            .is_empty()
    );
    let (_, health) = vfs.db().health(item.id).await.unwrap().unwrap();
    assert_eq!(health.recheck, infinidysk_db::Recheck::None);
    drop(queue);
    let restarted = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    assert_eq!(restarted.status().await.reserved_wire_bytes, Some(BLOCK));
    let before = server.body_commands();
    restarted.control(job.id, Operation::Pause).await.unwrap();
    restarted.control(job.id, Operation::Resume).await.unwrap();
    assert!(!restarted.tick(&policy).await.unwrap());
    assert_eq!(
        server.body_commands(),
        before,
        "spent daily quota cannot admit another BODY after restart"
    );
    policy.daily_wire_bytes = 100 * BLOCK;
    for _ in 0..4 {
        let _ = restarted.tick(&policy).await.unwrap();
    }
    // The operator resume made the retry due; a larger cap admits it without source replacement.
    restarted.control(job.id, Operation::Pause).await.unwrap();
    restarted.control(job.id, Operation::Resume).await.unwrap();
    for _ in 0..4 {
        let _ = restarted.tick(&policy).await.unwrap();
    }
    assert_eq!(restarted.status().await.jobs[0].state, State::Complete);
    let wire = restarted.status().await.received_wire_bytes;
    restarted.control(job.id, Operation::Retry).await.unwrap();
    for _ in 0..4 {
        let _ = restarted.tick(&policy).await.unwrap();
    }
    assert_eq!(restarted.status().await.jobs[0].state, State::Complete);
    assert_eq!(restarted.status().await.received_wire_bytes, wire);
}

#[tokio::test]
async fn warming_history_keeps_prior_attempt_bytes_and_does_not_change_the_block_checkpoint_ledger()
{
    let t = Temp::new();
    let (vfs, item, _cache, _corpus, _server, _client) = fixture(&t, false).await;
    let db = vfs.db().clone();
    let queue = Queue::load(db.clone(), vfs.clone()).await.unwrap();
    let job = enqueue(&queue, &item).await;
    for _ in 0..4 {
        let _ = queue.tick(&settings()).await.unwrap();
    }
    let status = queue.status().await;
    assert!(status.history_available);
    assert_eq!(status.history.len(), 1);
    assert_eq!(status.history[0].job.source_bytes, 2 * BLOCK);
    assert_eq!(status.history[0].job.revision, job.revision);
    queue.control(job.id, Operation::Retry).await.unwrap();
    for _ in 0..4 {
        let _ = queue.tick(&settings()).await.unwrap();
    }
    let status = queue.status().await;
    assert_eq!(status.history.len(), 2);
    let fresh = status.history.iter().find(|h| h.job.run == 1).unwrap();
    assert_eq!(fresh.job.source_bytes, 0);
    assert_eq!(fresh.job.cached_bytes, 2 * BLOCK);
    let ledger = db
        .config()
        .await
        .unwrap()
        .into_iter()
        .find(|(k, _)| k == "fork_prefetch_v1")
        .unwrap()
        .1;
    assert!(
        !ledger.contains("recorded_at"),
        "each block checkpoint must not rewrite outcome history"
    );
    drop(queue);
    let resumed = Queue::load(db, vfs).await.unwrap();
    assert_eq!(resumed.status().await.history.len(), 2);
}

#[tokio::test]
async fn retained_verified_articles_warm_final_media_without_provider_or_wire_reservations() {
    let t = Temp::new();
    let (vfs, item, cache, corpus, server, client) = fixture(&t, false).await;
    let decoded = Arc::new(infinidysk_stream::Cache::new(true, 16_000_000));
    for (article, data) in corpus.articles.iter().zip(corpus.data.chunks(256 * 1024)) {
        decoded.insert_revision(
            &article.message_id,
            infinidysk_nntp::RepairRevision::Base,
            bytes::Bytes::copy_from_slice(data),
        );
    }
    client.replace([]);
    let vfs = Arc::new(Arc::try_unwrap(vfs).ok().unwrap().with_cache(decoded));
    let queue = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    let _ = enqueue(&queue, &item).await;
    for _ in 0..4 {
        let _ = queue.tick(&settings()).await.unwrap();
    }
    let status = queue.status().await;
    assert_eq!(status.jobs[0].state, State::Complete);
    assert_eq!(server.body_commands(), 0);
    assert_eq!(status.received_wire_bytes, 0);
    assert_eq!(status.reserved_wire_bytes, Some(0));
    assert_eq!(
        cache
            .coverage(&vfs.cache_key(&item).await.unwrap())
            .await
            .unwrap()[0]
            .end,
        2 * BLOCK
    );
}

#[tokio::test]
async fn watched_full_file_keeps_its_lane_until_all_remaining_bytes_are_cached() {
    let t = Temp::new();
    let (vfs, item, _cache, _corpus, _server, _client) = fixture(&t, false).await;
    let mut other = item.clone();
    other.id = ItemId::new();
    other.name = "other-file.mkv".into();
    other.path = format!("{}/other-file.mkv", item.path.rsplit_once('/').unwrap().0);
    let bytes = vfs
        .db()
        .blob(item.meta_blob.unwrap())
        .await
        .unwrap()
        .unwrap();
    let blob_id = BlobId::new();
    other.meta_blob = Some(blob_id);
    vfs.db()
        .insert(&[other.clone()], &[(blob_id, &bytes)])
        .await
        .unwrap();
    let q = Queue::load(vfs.db().clone(), Arc::clone(&vfs))
        .await
        .unwrap();
    let watched = enqueue(&q, &item).await;
    let urgent = q
        .enqueue(
            other.id,
            &[Range {
                start: 0,
                end: other.size.unwrap(),
            }],
            100,
            Uuid::new_v4(),
        )
        .await
        .unwrap();
    let mut read = vfs
        .open(
            &Node::Item(item.clone()),
            Origin {
                delivery: Delivery::WebDav,
                client: "fixture-player".into(),
                player: None,
            },
        )
        .await
        .unwrap()
        .unwrap();
    read.read_exact(&mut [0u8; 1]).await.unwrap();
    drop(read);
    // Short handle gaps retain the observed position. Foreground work still wins.
    for _ in 0..100 {
        if q.tick(&settings()).await.unwrap() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    let status = q.status().await;
    assert_eq!(
        status
            .jobs
            .iter()
            .find(|j| j.id == watched.id)
            .unwrap()
            .verified_bytes,
        BLOCK
    );
    assert_eq!(
        status
            .jobs
            .iter()
            .find(|j| j.id == urgent.id)
            .unwrap()
            .verified_bytes,
        0
    );
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(
        q.status()
            .await
            .jobs
            .iter()
            .find(|j| j.id == watched.id)
            .unwrap()
            .state,
        State::Complete
    );
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(
        q.status()
            .await
            .jobs
            .iter()
            .find(|j| j.id == urgent.id)
            .unwrap()
            .verified_bytes,
        BLOCK
    );
}

#[tokio::test]
async fn cache_recovery_holds_warming_without_provider_or_daily_allowance_consumption() {
    let t = Temp::new();
    let (vfs, item, cache, _corpus, server, client) = fixture(&t, false).await;
    let db = vfs.db().clone();
    drop(vfs);
    drop(cache);
    let cache = Cache::open(Settings {
        enabled: true,
        recovery_mode: true,
        min_file_mb: 0,
        metadata_path: t.0.join("metadata").to_string_lossy().into_owned(),
        folders: vec![FolderSettings {
            id: Uuid::from_u128(1),
            path: t.0.join("cache").to_string_lossy().into_owned(),
            min_free_bytes: 0,
            ..Default::default()
        }],
        ..Default::default()
    })
    .await
    .unwrap();
    let vfs = Arc::new(
        Vfs::new(
            db,
            client,
            Budget::new(8 * 1024 * 1024),
            0,
            "/unused-test-mount",
        )
        .with_native_cache(Some(cache)),
    );
    let q = Queue::load(vfs.db().clone(), vfs).await.unwrap();
    enqueue(&q, &item).await;
    assert!(!q.tick(&settings()).await.unwrap());
    let status = q.status().await;
    assert!(status.recovery_mode);
    assert_eq!(server.body_commands(), 0);
    assert_eq!(status.charged_fill_bytes, 0);
    assert_eq!(status.received_wire_bytes, 0);
    assert_eq!(status.reserved_wire_bytes, Some(0));
    assert_eq!(status.jobs[0].state, State::Queued);
}

#[tokio::test]
async fn actual_uncached_playback_ranges_become_durable_urgent_backfills_without_replaying_probes()
{
    use infinidysk_prefetch::Family;
    let t = Temp::new();
    let (vfs, item, cache, corpus, _server, _client) = fixture_sized(&t, false, 0, 96).await;
    let queue = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    let mut held = Vec::new();
    while let Some(permit) = cache.try_buffer() {
        held.push(permit);
    }
    let origin = Origin {
        delivery: Delivery::WebDav,
        client: "fixture-player".into(),
        player: None,
    };
    let mut probe = vfs
        .open(&Node::Item(item.clone()), origin.clone())
        .await
        .unwrap()
        .unwrap();
    probe.seek(SeekFrom::Start(20 * 1024 * 1024)).await.unwrap();
    probe.read_exact(&mut [0; 64]).await.unwrap();
    drop(probe);
    assert_eq!(
        vfs.backfills().stats().0,
        0,
        "seek offsets do not qualify a probe"
    );
    let mut playback = vfs
        .open(&Node::Item(item.clone()), origin)
        .await
        .unwrap()
        .unwrap();
    let mut data = vec![0; 4 * infinidysk_native_cache::BLOCK_BYTES];
    playback.read_exact(&mut data).await.unwrap();
    drop(playback);
    assert_eq!(data, corpus.data[..data.len()]);
    assert_eq!(vfs.backfills().stats().0, 1);
    assert_eq!(
        cache.stats().await.unwrap().bytes,
        0,
        "buffer pressure left actual served bytes uncached"
    );
    drop(held);
    tokio::time::sleep(Duration::from_millis(2100)).await;
    queue
        .observe_backfills(&WarmSettings::default())
        .await
        .unwrap();
    assert!(queue.status().await.jobs.is_empty());
    let full = enqueue(&queue, &item).await;
    queue.observe_backfills(&settings()).await.unwrap();
    let status = queue.status().await;
    let backfill = status
        .jobs
        .iter()
        .find(|j| j.family == Family::Backfill)
        .unwrap();
    let id = backfill.id;
    assert_eq!(
        backfill.ranges,
        vec![Range {
            start: 0,
            end: 4 * BLOCK
        }]
    );
    assert_eq!(backfill.priority, 100);
    assert_eq!(status.pending_backfills, 0);
    drop(queue);
    let queue = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    queue.reconcile_automatic(None, None).await.unwrap();
    queue.observe_reads(&settings()).await.unwrap();
    assert!(queue.tick(&settings()).await.unwrap());
    let status = queue.status().await;
    assert_eq!(
        status
            .jobs
            .iter()
            .find(|j| j.id == id)
            .unwrap()
            .verified_bytes,
        BLOCK,
        "own-file backfill precedes a full-file policy"
    );
    assert_eq!(
        status
            .jobs
            .iter()
            .find(|j| j.id == full.id)
            .unwrap()
            .verified_bytes,
        0
    );
    queue
        .observe_backfills(&WarmSettings::default())
        .await
        .unwrap();
    let status = queue.status().await;
    assert_eq!(
        status.jobs.iter().find(|j| j.id == id).unwrap().state,
        State::Cancelled
    );
    assert_eq!(
        status.jobs.iter().find(|j| j.id == full.id).unwrap().state,
        State::Queued,
        "policy cleanup preserves manual work"
    );
}

#[tokio::test]
async fn backfill_admission_fences_changed_sources_and_keeps_hints_after_database_failure() {
    for changed_source in [true, false] {
        let t = Temp::new();
        let (vfs, item, cache, _corpus, _server, _client) = fixture_sized(&t, false, 0, 64).await;
        let queue = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
        let mut held = Vec::new();
        while let Some(permit) = cache.try_buffer() {
            held.push(permit);
        }
        let mut reader = vfs
            .open(
                &Node::Item(item.clone()),
                Origin {
                    delivery: Delivery::Fuse,
                    client: "fixture-player".into(),
                    player: None,
                },
            )
            .await
            .unwrap()
            .unwrap();
        reader
            .read_exact(&mut vec![0; 4 * infinidysk_native_cache::BLOCK_BYTES])
            .await
            .unwrap();
        drop(reader);
        drop(held);
        tokio::time::sleep(Duration::from_millis(2100)).await;
        assert_eq!(vfs.backfills().stats().0, 1);
        if changed_source {
            let raw = vfs
                .db()
                .blob(item.meta_blob.unwrap())
                .await
                .unwrap()
                .unwrap();
            let mut meta = blob::decode_meta(&raw).unwrap();
            if let FileMeta::Nzb(file) = &mut meta {
                file.segments.items[0].id = "replacement@test.invalid".into();
            }
            vfs.db()
                .update_blob(item.meta_blob.unwrap(), &blob::encode_meta(&meta).unwrap())
                .await
                .unwrap();
            queue.observe_backfills(&settings()).await.unwrap();
            assert!(queue.status().await.jobs.is_empty());
            assert_eq!(vfs.backfills().stats().0, 0);
        } else {
            vfs.db().close().await;
            assert!(queue.observe_backfills(&settings()).await.is_err());
            assert_eq!(
                vfs.backfills().stats().0,
                1,
                "failed durable admission cannot acknowledge a playback hint"
            );
        }
    }
}
