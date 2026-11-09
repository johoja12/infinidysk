//! Shared article repair changes final-media identity across deliveries and dependent
//! items, while unrelated native-cache generations remain reusable.
use bytes::Bytes;
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, Item};
use infinidysk_native_cache::repair::{Geometry, Patch, Proof, Store};
use infinidysk_native_cache::{Cache, FolderSettings, Settings};
use infinidysk_nntp::Client;
use infinidysk_stream::Budget;
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use std::{io::SeekFrom, path::PathBuf, sync::Arc, time::Duration};
use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _};
use uuid::Uuid;
struct Fixture {
    path: PathBuf,
    vfs: Vfs,
    store: Arc<Store>,
    cache: Arc<Cache>,
    files: Vec<Item>,
}
fn origin(delivery: Delivery) -> Origin {
    Origin {
        delivery,
        client: "fixture".into(),
        player: None,
    }
}
async fn fixture() -> Fixture {
    let path = std::env::temp_dir().join(format!("infinidysk-revision-vfs-{}", Uuid::new_v4()));
    std::fs::create_dir_all(path.join("payload")).expect("directory");
    let store = Store::open(&path.join("metadata")).await.expect("ledger");
    let cache = Cache::open(Settings {
        enabled: true,
        min_file_mb: 0,
        metadata_path: path.join("metadata").to_str().expect("path").into(),
        folders: vec![FolderSettings {
            id: Uuid::new_v4(),
            path: path.join("payload").to_str().expect("path").into(),
            min_free_bytes: 0,
            ..FolderSettings::default()
        }],
        ..Settings::default()
    })
    .await
    .expect("native cache");
    let db = Db::connect(&format!("sqlite:{}", path.join("files.sqlite").display()))
        .await
        .expect("database");
    let root = db
        .item(ItemId::CONTENT)
        .await
        .expect("root")
        .expect("root item");
    let mut files = Vec::new();
    for (i, id) in ["shared@fixture", "shared@fixture", "unrelated@fixture"]
        .iter()
        .enumerate()
    {
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: (*id).into(),
                    size: 8,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                missing: if i == 1 { vec![0] } else { vec![] },
                ..Segments::default()
            },
            proof: None,
        });
        let mut file =
            Item::child(&root, &format!("fixture-{i}.bin"), ItemKind::NzbFile).expect("file");
        let blob_id = BlobId::new();
        file.meta_blob = Some(blob_id);
        file.size = Some(8);
        db.insert(
            std::slice::from_ref(&file),
            &[(blob_id, &blob::encode_meta(&meta).expect("metadata"))],
        )
        .await
        .expect("insert");
        files.push(file);
    }
    let client = Arc::new(Client::new([]));
    assert!(client.set_repairs(store.clone()));
    let vfs = Vfs::new(db, client, Budget::new(1 << 20), 0, "/unused-fixture-mount")
        .with_native_cache(Some(cache.clone()));
    Fixture {
        path,
        vfs,
        store,
        cache,
        files,
    }
}
fn patch() -> Patch {
    Patch {
        message_id: "shared@fixture".into(),
        geometry: Geometry {
            offset: 0,
            file_size: 8,
            part: Some(1),
            total: Some(1),
        },
        proof: Proof {
            set: [1; 16],
            file: [2; 16],
            file_md5: [3; 16],
            file_length: 8,
        },
        data: Bytes::from_static(b"restored"),
    }
}
#[tokio::test]
async fn changed_articles_retire_dependent_handles_but_leave_unrelated_cached_reads_working() {
    let f = fixture().await;
    let original = Vfs::new(
        f.vfs.db().clone(),
        Arc::new(Client::new([])),
        Budget::new(1 << 20),
        0,
        "/unused-fixture-mount",
    );
    for file in &f.files {
        assert_eq!(
            f.vfs
                .cache_key(file)
                .await
                .expect("empty repair ledger key"),
            original.cache_key(file).await.expect("original key"),
            "unmodified sources preserve existing 1.x cache import identity"
        );
    }
    drop(original);
    let mut keys = Vec::new();
    for file in &f.files {
        let key = f.vfs.cache_key(file).await.expect("old key");
        let lease = f.cache.lease(key.clone()).expect("lease");
        assert!(
            f.cache
                .put(&lease, 0, Bytes::from_static(b"original"))
                .await
                .expect("seed old bytes")
        );
        keys.push(key);
    }
    let mut first = f
        .vfs
        .open(&Node::Item(f.files[0].clone()), origin(Delivery::WebDav))
        .await
        .expect("open")
        .expect("file");
    let mut dependent = f
        .vfs
        .open(&Node::Item(f.files[1].clone()), origin(Delivery::Fuse))
        .await
        .expect("open dependent")
        .expect("file");
    let mut unrelated = f
        .vfs
        .open(&Node::Item(f.files[2].clone()), origin(Delivery::Fuse))
        .await
        .expect("open unrelated")
        .expect("file");
    let mut prefix = [0; 2];
    first
        .read_exact(&mut prefix)
        .await
        .expect("old cached prefix");
    assert_eq!(&prefix, b"or");
    f.store
        .publish(vec![patch()])
        .await
        .expect("publish repair");
    assert_ne!(
        f.vfs.cache_key(&f.files[0]).await.expect("new key"),
        keys[0]
    );
    assert_ne!(
        f.vfs.cache_key(&f.files[1]).await.expect("dependent key"),
        keys[1]
    );
    assert_eq!(
        f.vfs.cache_key(&f.files[2]).await.expect("unrelated key"),
        keys[2]
    );
    let mut blocked = [99; 2];
    assert!(first.read_exact(&mut blocked).await.is_err());
    assert_eq!(blocked, [99; 2]);
    assert!(
        dependent.read_exact(&mut blocked).await.is_err(),
        "even an unpolled dependent reader keeps its original key stamp"
    );
    unrelated
        .seek(SeekFrom::Start(0))
        .await
        .expect("unrelated seek");
    let mut bytes = Vec::new();
    unrelated
        .read_to_end(&mut bytes)
        .await
        .expect("unrelated cached read");
    assert_eq!(bytes, b"original");
    drop(first);
    drop(dependent);
    drop(unrelated);
    for (file, delivery) in f.files[..2].iter().zip([Delivery::WebDav, Delivery::Fuse]) {
        let mut reader = f
            .vfs
            .open(&Node::Item(file.clone()), origin(delivery))
            .await
            .expect("new open")
            .expect("file");
        let mut bytes = Vec::new();
        reader
            .read_to_end(&mut bytes)
            .await
            .expect("repaired data including recorded holes");
        assert_eq!(bytes, b"restored");
    }
    let path = f.path.clone();
    drop(f);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn persisted_repair_keys_survive_ledger_restart_and_valid_native_data_outlives_patch_eviction()
 {
    let f = fixture().await;
    f.store.publish(vec![patch()]).await.expect("repair");
    let key = f.vfs.cache_key(&f.files[0]).await.expect("repaired key");
    {
        let mut reader = f
            .vfs
            .open(&Node::Item(f.files[0].clone()), origin(Delivery::WebDav))
            .await
            .expect("open")
            .expect("file");
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes).await.expect("repair bytes");
        assert_eq!(bytes, b"restored");
    }
    tokio::time::timeout(Duration::from_secs(5), async {
        while f.cache.coverage(&key).await.expect("coverage").is_empty() {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("final-media fill");
    f.store
        .evict_payload("shared@fixture")
        .await
        .expect("retention eviction");
    let Fixture {
        path,
        vfs,
        store,
        cache,
        files,
    } = f;
    let db = vfs.db().clone();
    drop(vfs);
    drop(store);
    let store = Store::open(&path.join("metadata"))
        .await
        .expect("ledger restart");
    assert!(
        store.generation() > 0,
        "persisted repairs are visible before another publication"
    );
    let client = Arc::new(Client::new([]));
    assert!(client.set_repairs(store.clone()));
    let vfs = Vfs::new(db, client, Budget::new(1 << 20), 0, "/unused-fixture-mount")
        .with_native_cache(Some(cache.clone()));
    assert_eq!(vfs.cache_key(&files[0]).await.expect("stable key"), key);
    let mut reader = vfs
        .open(&Node::Item(files[0].clone()), origin(Delivery::Fuse))
        .await
        .expect("open")
        .expect("file");
    let mut bytes = Vec::new();
    reader
        .read_to_end(&mut bytes)
        .await
        .expect("verified final-media bytes without patch/provider");
    assert_eq!(bytes, b"restored");
    drop(reader);
    drop(vfs);
    drop(store);
    drop(cache);
    std::fs::remove_dir_all(path).expect("cleanup");
}
