//! The same final-media cache serves every delivery through VFS, with cold fallback.
#![allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, Item};
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, FolderSettings, Settings};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_stream::Budget;
use infinidysk_testkit::{Corpus, LoopbackServer};
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use std::io::SeekFrom;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncSeekExt};

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
async fn fixture(t: &Temp, missing: bool) -> (Vfs, Node, Arc<Cache>, Corpus, LoopbackServer) {
    let corpus = Corpus::generate(1025, 16, 256 * 1024);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
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
    item.size = Some(BLOCK);
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
        client,
        Budget::new(8 * 1024 * 1024),
        1024 * 1024,
        "/unused-test-mount",
    )
    .with_native_cache(Some(Arc::clone(&cache)));
    (vfs, Node::Item(item), cache, corpus, server)
}
fn origin(delivery: Delivery) -> Origin {
    Origin {
        delivery,
        client: "test".to_owned(),
        player: None,
    }
}

#[tokio::test]
async fn warm_bytes_and_seeks_are_shared_by_webdav_and_fuse_without_provider_reads() {
    let t = Temp::new();
    let (vfs, node, cache, corpus, server) = fixture(&t, false).await;
    let mut cold = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    let mut first = [0; 64];
    cold.read_exact(&mut first).await.unwrap();
    assert_eq!(first, corpus.data[..64]);
    assert_eq!(
        cache.stats().await.unwrap().bytes,
        0,
        "first bytes do not wait for a whole cache block"
    );
    let mut rest = Vec::new();
    cold.read_to_end(&mut rest).await.unwrap();
    assert_eq!(rest, corpus.data[64..]);
    drop(cold);
    tokio::time::timeout(Duration::from_secs(5), async {
        while cache.stats().await.unwrap().bytes < BLOCK {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    let before = server.body_commands();
    for delivery in [Delivery::WebDav, Delivery::Fuse] {
        let mut reader = vfs.open(&node, origin(delivery)).await.unwrap().unwrap();
        for offset in [BLOCK - 1024, 17, BLOCK / 2, 0] {
            reader.seek(SeekFrom::Start(offset)).await.unwrap();
            let mut bytes = [0; 512];
            reader.read_exact(&mut bytes).await.unwrap();
            assert_eq!(
                bytes,
                corpus.data
                    [usize::try_from(offset).unwrap()..usize::try_from(offset).unwrap() + 512]
            );
        }
    }
    assert_eq!(
        server.body_commands(),
        before,
        "cache hits make no provider BODY requests"
    );
    assert!(vfs.sessions().open().is_empty());
    let rows = vfs.sessions().playback();
    assert_eq!(rows.len(), 3);
    assert_eq!(rows.iter().map(|r| r.native_cache_bytes).sum::<u64>(), 4096);
    assert_eq!(rows.iter().map(|r| r.served).sum::<u64>(), BLOCK + 4096);
    assert_eq!(vfs.sessions().take_ended().len(), 3);
    assert_eq!(
        vfs.sessions().playback().len(),
        3,
        "metrics draining cannot consume burst attribution"
    );
}

#[tokio::test]
async fn playback_with_a_filled_gap_never_publishes_it_as_verified_cache_data() {
    let t = Temp::new();
    let (vfs, node, cache, _, _server) = fixture(&t, true).await;
    vfs.set_gap_tolerance(false);
    let mut reader = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    let mut bytes = Vec::new();
    reader.read_to_end(&mut bytes).await.unwrap();
    assert_eq!(bytes.len() as u64, BLOCK);
    drop(reader);
    tokio::time::sleep(Duration::from_millis(30)).await;
    assert_eq!(
        cache.stats().await.unwrap().bytes,
        0,
        "replacement/filler bytes are never a cache hit"
    );
}

#[tokio::test]
async fn broken_payload_falls_back_to_the_source_and_keeps_the_response_correct() {
    let t = Temp::new();
    let (vfs, node, cache, corpus, _server) = fixture(&t, false).await;
    let Node::Item(item) = &node else {
        unreachable!()
    };
    let key = vfs.cache_key(item).await.unwrap();
    let lease = cache.lease(key.clone()).unwrap();
    cache
        .put(&lease, 0, bytes::Bytes::copy_from_slice(&corpus.data))
        .await
        .unwrap();
    drop(lease);
    let path =
        t.0.join("cache")
            .join(format!("{}-{}-0.data", key.item().simple(), key.revision()));
    std::fs::write(path, vec![0; BLOCK_BYTES]).unwrap();
    let mut reader = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    reader.seek(SeekFrom::Start(100)).await.unwrap();
    let mut got = [0; 512];
    reader.read_exact(&mut got).await.unwrap();
    assert_eq!(got, corpus.data[100..612]);
}

#[tokio::test]
async fn availability_updates_keep_verified_bytes_and_do_not_ask_providers() {
    let t = Temp::new();
    let (vfs, node, cache, corpus, server) = fixture(&t, false).await;
    let Node::Item(item) = &node else {
        unreachable!()
    };
    let key = vfs.cache_key(item).await.unwrap();
    let lease = cache.lease(key).unwrap();
    cache
        .put(&lease, 0, bytes::Bytes::copy_from_slice(&corpus.data))
        .await
        .unwrap();
    drop(lease);
    let id = item.meta_blob.unwrap();
    let mut meta = blob::decode_meta(&vfs.db().blob(id).await.unwrap().unwrap()).unwrap();
    let FileMeta::Nzb(file) = &mut meta else {
        unreachable!()
    };
    file.segments.missing = vec![4];
    file.segments.corrupt = vec![5];
    vfs.db()
        .update_blob(id, &blob::encode_meta(&meta).unwrap())
        .await
        .unwrap();
    let before = server.body_commands();
    let mut reader = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    reader.seek(SeekFrom::Start(4 * 256 * 1024)).await.unwrap();
    let mut bytes = [0; 512];
    reader.read_exact(&mut bytes).await.unwrap();
    assert_eq!(bytes, corpus.data[4 * 256 * 1024..4 * 256 * 1024 + 512]);
    assert_eq!(server.body_commands(), before);
}

#[tokio::test]
async fn overlapping_cached_reads_keep_repair_and_eviction_protection_until_last_close() {
    let t = Temp::new();
    let (vfs, node, cache, corpus, _server) = fixture(&t, false).await;
    let Node::Item(item) = &node else {
        unreachable!()
    };
    let key = vfs.cache_key(item).await.unwrap();
    let lease = cache.lease(key.clone()).unwrap();
    cache
        .put(&lease, 0, bytes::Bytes::copy_from_slice(&corpus.data))
        .await
        .unwrap();
    drop(lease);
    let first = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    let last = vfs
        .open(&node, origin(Delivery::Fuse))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(vfs.sessions().open().len(), 2);
    drop(first);
    assert_eq!(vfs.sessions().open().len(), 1);
    assert!(!cache.evict(&key, "test").await.unwrap());
    drop(last);
    assert!(vfs.sessions().open().is_empty());
    assert!(cache.evict(&key, "test").await.unwrap());
}

async fn member_cache_case(encrypted: bool) {
    let t = Temp::new();
    let (vfs, plain, cache, corpus, server) = fixture(&t, false).await;
    let Node::Item(plain) = plain else {
        unreachable!()
    };
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
    let node = Node::Item(item);
    let mut reader = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    let mut actual = Vec::new();
    reader.read_to_end(&mut actual).await.unwrap();
    assert_eq!(actual, expected);
    drop(reader);
    tokio::time::timeout(Duration::from_secs(5), async {
        while cache.stats().await.unwrap().bytes < expected.len() as u64 {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    let before = server.body_commands();
    let mut cached = vfs
        .open(&node, origin(Delivery::Fuse))
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
async fn stored_member_ranges_are_cached_as_final_bytes() {
    member_cache_case(false).await;
}

#[tokio::test]
async fn encrypted_members_are_cached_and_seeked_as_plaintext() {
    member_cache_case(true).await;
}

#[tokio::test]
async fn repair_exclusion_blocks_new_delivery_handles_and_rechecks_stale_nodes() {
    let t = Temp::new();
    let (vfs, node, _cache, _corpus, _server) = fixture(&t, false).await;
    let Node::Item(item) = &node else {
        unreachable!()
    };
    let reader = vfs
        .open(&node, origin(Delivery::WebDav))
        .await
        .unwrap()
        .unwrap();
    assert!(vfs.sessions().try_repair(item.id).is_none());
    drop(reader);
    let guard = vfs.sessions().try_repair(item.id).unwrap();
    for delivery in [Delivery::Fuse, Delivery::WebDav] {
        assert!(matches!(
            vfs.open(&node, origin(delivery)).await,
            Err(infinidysk_vfs::Error::Repairing)
        ));
    }
    drop(guard);
    vfs.db().delete_item(item.id).await.unwrap();
    assert!(
        matches!(
            vfs.open(&node, origin(Delivery::Fuse)).await,
            Err(infinidysk_vfs::Error::Repairing)
        ),
        "a stale inode must not revive removed content"
    );
    assert!(vfs.sessions().open().is_empty());
}
