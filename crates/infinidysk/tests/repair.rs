//! Bootstrap and publication against actual main/native/decoded metadata stores.
use axum::body::Bytes;
use infinidysk::repair::open;
use infinidysk_db::Db;
use infinidysk_native_cache::{
    Cache as NativeCache, Settings,
    repair::{Geometry, Patch, Proof, Revision, Store},
};
use infinidysk_stream::{Cache, CacheConfig, DiskConfig};
use std::{path::PathBuf, sync::Arc, time::Duration};
use uuid::Uuid;
fn directory() -> PathBuf {
    let path = std::env::temp_dir().join(format!("infinidysk-repair-bootstrap-{}", Uuid::new_v4()));
    std::fs::create_dir(&path).expect("directory");
    path
}
fn patch() -> Patch {
    Patch {
        message_id: "fixture@loopback".into(),
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
async fn a_default_install_creates_no_ledger_but_disabling_attempts_preserves_existing_repairs() {
    let path = directory();
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("main");
    let cache = Arc::new(Cache::new(true, 1 << 20));
    assert!(
        open(&path, db.clone(), cache.clone(), None, false)
            .await
            .expect("disabled")
            .is_none()
    );
    assert!(!path.join("repair-v2").exists());
    std::fs::create_dir(path.join("repair-v2")).expect("empty precreated directory");
    assert!(
        open(&path, db.clone(), cache.clone(), None, false)
            .await
            .expect("empty directory is not activation")
            .is_none()
    );
    assert!(!path.join("repair-v2/catalogue.sqlite").exists());

    let store = open(&path, db.clone(), cache.clone(), None, true)
        .await
        .expect("create")
        .expect("store");
    store
        .publish(vec![patch()])
        .await
        .expect("verified publication");
    let id = store.id();
    let revision = store.revision("fixture@loopback");
    drop(store);
    let store = open(&path, db.clone(), cache, None, false)
        .await
        .expect("disabled attempts still open existing state")
        .expect("store");
    assert_eq!(store.id(), id);
    assert_eq!(store.revision("fixture@loopback"), revision);
    assert_eq!(store.epoch(), 1);
    assert!(matches!(revision, Revision::Repaired(_)));
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn a_missing_bound_ledger_is_not_replaced_by_an_empty_one() {
    let path = directory();
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("main");
    assert!(
        db.bind_repair_lineage(Uuid::new_v4(), 3)
            .await
            .expect("previous identity")
    );
    assert!(
        open(
            &path,
            db.clone(),
            Arc::new(Cache::new(true, 1 << 20)),
            None,
            false
        )
        .await
        .is_err()
    );
    assert!(!path.join("repair-v2").exists());
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn startup_refuses_newer_external_cache_state_even_after_both_local_ledgers_are_lost() {
    let path = directory();
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("main");
    let cache = Arc::new(Cache::new(false, 1 << 20));
    let config = CacheConfig {
        memory_enabled: false,
        memory_bytes: 1 << 20,
        disk: Some(DiskConfig {
            dir: path.join("decoded"),
            max_bytes: 1 << 30,
            max_age: Duration::from_secs(3600),
        }),
    };
    cache.configure(&config).await;
    let store = open(&path, db.clone(), cache.clone(), None, true)
        .await
        .expect("initialize")
        .expect("store");
    store.publish(vec![patch()]).await.expect("publication");
    drop(store);
    drop(cache);
    drop(db);
    std::fs::remove_dir_all(path.join("repair-v2")).expect("simulate lost local repair data");
    let fresh_db = Db::connect(&format!("sqlite:{}", path.join("fresh.sqlite").display()))
        .await
        .expect("fresh main");
    let fresh = Arc::new(Cache::new(false, 1 << 20));
    fresh.configure(&config).await;
    assert!(
        open(&path, fresh_db.clone(), fresh.clone(), None, true)
            .await
            .is_err(),
        "a retained external witness prevents base-version resurrection"
    );
    assert!(
        fresh_db
            .repair_lineage()
            .await
            .expect("no poisoning of fresh main state")
            .is_none()
    );
    assert!(
        !path.join("repair-v2").exists(),
        "do not manufacture an empty replacement before checking existing witnesses"
    );
    drop(fresh);
    drop(fresh_db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn bootstrap_checks_independent_native_metadata_and_publication_updates_every_witness() {
    let path = directory();
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("main");
    let native = NativeCache::open(Settings {
        metadata_path: path.join("native-meta").to_str().expect("path").into(),
        ..Settings::default()
    })
    .await
    .expect("native");
    let cache = Arc::new(Cache::new(true, 1 << 20));
    let store = open(&path, db.clone(), cache.clone(), Some(native.clone()), true)
        .await
        .expect("bootstrap")
        .expect("store");
    store.publish(vec![patch()]).await.expect("publication");
    let id = store.id();
    assert_eq!(
        db.repair_lineage()
            .await
            .expect("main witness")
            .expect("binding")
            .epoch,
        1
    );
    assert!(native.bind_repair_lineage(id, 0).await.is_err());
    assert!(cache.bind_repair_lineage(*id.as_bytes(), 0).await.is_err());
    drop(store);
    drop(cache);
    drop(native);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn an_existing_unbound_ledger_is_adopted_without_needing_new_attempts() {
    let path = directory();
    let store = Store::open(&path.join("repair-v2"))
        .await
        .expect("preserved ledger");
    store
        .publish(vec![patch()])
        .await
        .expect("previous verified data");
    let id = store.id();
    drop(store);
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("main");
    let store = open(
        &path,
        db.clone(),
        Arc::new(Cache::new(true, 1 << 20)),
        None,
        false,
    )
    .await
    .expect("adopt")
    .expect("store");
    assert_eq!(store.id(), id);
    assert_eq!(store.epoch(), 1);
    assert_eq!(
        db.repair_lineage()
            .await
            .expect("binding")
            .expect("bound")
            .id,
        id
    );
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
