//! Real SQLite repair source plus loopback NNTP, including delayed/drained originals.
use super::*;
use infinidysk_native_cache::repair::{Geometry, Patch, Proof, Store};
use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_testkit::{LoopbackServer, post};
use uuid::Uuid;
fn directory() -> PathBuf {
    let path = std::env::temp_dir().join(format!("infinidysk-stream-repairs-{}", Uuid::new_v4()));
    std::fs::create_dir(&path).expect("directory");
    path
}
fn patch(id: &str) -> Patch {
    Patch {
        message_id: id.into(),
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
fn client(server: &LoopbackServer) -> (Arc<Client>, Arc<Downloads>) {
    let downloads = Downloads::new(8);
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .expect("endpoint");
    let pool = Pool::new(
        endpoint,
        PoolConfig {
            max_connections: 2,
            min_connections: 0,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
        },
        downloads.clone(),
    )
    .expect("pool");
    (
        Arc::new(Client::new([Provider {
            pool,
            key: "fixture".into(),
            name: "fixture".into(),
            priority: 0,
            backbone: None,
        }])),
        downloads,
    )
}
#[tokio::test]
async fn a_new_repair_never_joins_an_old_provider_flight_or_reuses_its_cached_body() {
    let path = directory();
    let store = Store::open(&path.join("metadata")).await.expect("ledger");
    let articles = post("revision", "fixture.bin", b"original", 8);
    let id = articles[0].message_id.clone();
    let (server, mut gate) = LoopbackServer::start_gated(articles).await.expect("server");
    let (client, _) = client(&server);
    assert!(client.set_repairs(store.clone()));
    let cache = Arc::new(Cache::new(true, 1 << 20));
    cache.insert("untouched@fixture", Bytes::from_static(b"retained"));
    let old = {
        let cache = cache.clone();
        let client = client.clone();
        let id = id.clone();
        tokio::spawn(async move { cache.body(&client, &id, Some(0), None, None).await })
    };
    tokio::time::timeout(Duration::from_secs(5), gate.blocked())
        .await
        .expect("original held");
    store
        .publish(vec![patch(&id)])
        .await
        .expect("repair publication");
    let bytes = tokio::time::timeout(
        Duration::from_secs(2),
        cache.body(&client, &id, Some(0), None, None),
    )
    .await
    .expect("new revision is independent of held original")
    .expect("repair");
    assert_eq!(bytes.as_ref(), b"restored");
    assert_eq!(server.body_commands(), 1);
    gate.release();
    assert_eq!(
        old.await
            .expect("old reader")
            .expect("retries current revision")
            .as_ref(),
        b"restored"
    );
    cache.insert_revision(&id, RepairRevision::Base, Bytes::from_static(b"original"));
    assert_eq!(
        cache
            .body(&client, &id, Some(0), None, None)
            .await
            .expect("late base cannot win")
            .as_ref(),
        b"restored"
    );
    assert_eq!(
        cache
            .body(&client, "untouched@fixture", None, None, None)
            .await
            .expect("unrelated original key")
            .as_ref(),
        b"retained"
    );
    assert!(
        cache.body(&client, &id, Some(1), None, None).await.is_err(),
        "a hot repair still checks source geometry"
    );
    let poison = format!("{id}\0repair-v1\0malformed");
    cache.insert(&poison, Bytes::from_static(b"invalid!"));
    assert!(
        cache
            .body(&client, &poison, None, None, None)
            .await
            .is_err(),
        "raw IDs cannot collide with revision namespaces"
    );
    drop(cache);
    drop(client);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn abandoned_original_bodies_keep_their_admission_stamp_after_publication() {
    let path = directory();
    let store = Store::open(&path.join("metadata")).await.expect("ledger");
    let articles = post("drained", "fixture.bin", b"original", 8);
    let id = articles[0].message_id.clone();
    let (server, mut gate) = LoopbackServer::start_gated(articles).await.expect("server");
    let (client, downloads) = client(&server);
    assert!(client.set_repairs(store.clone()));
    let cache = Arc::new(Cache::new(true, 1 << 20));
    let seen = Arc::new(Mutex::new(Vec::new()));
    client.set_sink(Box::new({
        let cache = cache.clone();
        let seen = seen.clone();
        move |id, _, body, drained, revision| {
            cache.insert_revision(id, revision, body.data.clone());
            seen.lock().expect("sink").push((drained, revision));
        }
    }));
    let left = {
        let cache = cache.clone();
        let client = client.clone();
        let id = id.clone();
        tokio::spawn(async move { cache.body(&client, &id, Some(0), None, None).await })
    };
    tokio::time::timeout(Duration::from_secs(5), gate.blocked())
        .await
        .expect("original held");
    left.abort();
    assert!(left.await.expect_err("caller left").is_cancelled());
    store.publish(vec![patch(&id)]).await.expect("repair");
    gate.release();
    tokio::time::timeout(Duration::from_secs(5), async {
        while downloads.available_permits() != 8 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("drained original releases permit");
    assert!(
        seen.lock()
            .expect("sink")
            .contains(&(true, RepairRevision::Base))
    );
    assert_eq!(
        cache
            .body(&client, &id, Some(0), None, None)
            .await
            .expect("repair")
            .as_ref(),
        b"restored"
    );
    drop(cache);
    drop(client);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn versioned_disk_keys_survive_reopen_and_never_fall_back_to_old_base_bytes() {
    let path = directory();
    let store = Store::open(&path.join("metadata")).await.expect("ledger");
    let client = Arc::new(Client::new([]));
    assert!(client.set_repairs(store.clone()));
    let config = CacheConfig {
        memory_enabled: false,
        memory_bytes: 1 << 20,
        disk: Some(DiskConfig {
            dir: path.join("segments"),
            max_bytes: 1 << 30,
            max_age: Duration::from_secs(3600),
        }),
    };
    let cache = Arc::new(Cache::new(false, 1 << 20));
    cache.configure(&config).await;
    cache.insert("disk@fixture", Bytes::from_static(b"original"));
    store
        .publish(vec![patch("disk@fixture")])
        .await
        .expect("repair");
    assert_eq!(
        cache
            .body(&client, "disk@fixture", Some(0), None, None)
            .await
            .expect("repaired disk key")
            .as_ref(),
        b"restored"
    );
    tokio::time::timeout(Duration::from_secs(5), async {
        while cache
            .stats()
            .disk
            .is_none_or(|d| d.indexing || d.bytes < 16)
        {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("both namespaces committed");
    drop(cache);
    let cache = Arc::new(Cache::new(false, 1 << 20));
    cache.configure(&config).await;
    assert_eq!(
        cache
            .body(&client, "disk@fixture", Some(0), None, None)
            .await
            .expect("repair after reopen")
            .as_ref(),
        b"restored"
    );
    let no_disk = CacheConfig {
        disk: None,
        ..config
    };
    cache.configure(&no_disk).await;
    store
        .evict_payload("disk@fixture")
        .await
        .expect("evict payload only");
    assert!(
        cache
            .body(&client, "disk@fixture", Some(0), None, None)
            .await
            .is_err(),
        "absent repair must not return old original bytes"
    );
    drop(cache);
    drop(client);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn external_disk_lineage_rejects_forgotten_repairs_and_checks_runtime_path_changes() {
    let path = directory();
    let id = [17; 16];
    let config = |dir: &str| CacheConfig {
        memory_enabled: true,
        memory_bytes: 1 << 20,
        disk: Some(DiskConfig {
            dir: path.join(dir),
            max_bytes: 1 << 30,
            max_age: Duration::from_secs(3600),
        }),
    };
    let first = Arc::new(Cache::new(true, 1 << 20));
    first.configure(&config("first")).await;
    first
        .bind_repair_lineage(id, 0)
        .await
        .expect("initial binding");
    first
        .bind_repair_lineage(id, 3)
        .await
        .expect("forward witness");
    assert!(first.bind_repair_lineage(id, 2).await.is_err());
    assert!(first.bind_repair_lineage([18; 16], 4).await.is_err());
    let fresh = Cache::new(true, 1 << 20);
    fresh.configure(&config("first")).await;
    assert!(
        fresh.bind_repair_lineage(id, 0).await.is_err(),
        "a fresh process cannot forget previous repair epochs"
    );
    fresh
        .bind_repair_lineage(id, 3)
        .await
        .expect("matching restart");
    let foreign = Cache::new(true, 1 << 20);
    foreign.configure(&config("foreign")).await;
    foreign
        .bind_repair_lineage([19; 16], 1)
        .await
        .expect("other ledger");
    let before = std::fs::read(path.join("foreign/.repair-lineage")).expect("foreign witness");
    first.configure(&config("foreign")).await;
    assert!(first.stats().disk.is_none());
    assert!(first.stats().disk_failure.is_some());
    assert_eq!(
        std::fs::read(path.join("foreign/.repair-lineage")).expect("unchanged witness"),
        before
    );
    first.configure(&config("first")).await;
    assert!(first.stats().disk.is_some());
    drop(first);
    drop(fresh);
    drop(foreign);
    std::fs::remove_dir_all(path).expect("cleanup");
}
