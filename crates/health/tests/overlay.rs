//! A process restart after committed repair must not replace its newly readable
//! source before a fresh health check observes the authoritative article overlay.
use bytes::Bytes;
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, HealthState, Item, Recheck};
use infinidysk_health::{Health, Settings, UnownedAction};
use infinidysk_native_cache::repair::{Error, Geometry, Patch, Proof, Store};
use infinidysk_nntp::Client;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use tokio::sync::watch;
use uuid::Uuid;
async fn setup(count: usize) -> (Db, Item, Arc<Client>, Arc<Store>, std::path::PathBuf) {
    let path = std::env::temp_dir().join(format!("infinidysk-overlay-health-{}", Uuid::new_v4()));
    std::fs::create_dir(&path).expect("path");
    let store = Store::open(&path.join("repair")).await.expect("store");
    let db = Db::connect(&format!("sqlite:{}", path.join("main.sqlite").display()))
        .await
        .expect("db");
    let root = db.item(ItemId::CONTENT).await.expect("root").expect("item");
    let mut item = Item::child(&root, "fixture.mkv", ItemKind::NzbFile).expect("file");
    let meta = FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: (0..count)
                .map(|i| Segment {
                    id: format!("source-{i}@fixture"),
                    size: 8,
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            missing: (0..count)
                .map(|i| u32::try_from(i).expect("index"))
                .collect(),
            ..Segments::default()
        },
        proof: None,
    });
    let id = BlobId::new();
    item.meta_blob = Some(id);
    item.size = Some(meta.size());
    db.insert(
        std::slice::from_ref(&item),
        &[(id, &blob::encode_meta(&meta).expect("blob"))],
    )
    .await
    .expect("insert");
    let (_, mut status) = db.health(item.id).await.expect("status").expect("file");
    status.state = HealthState::Repairing;
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .expect("repair pending");
    let client = Arc::new(Client::new([]));
    assert!(client.set_repairs(store.clone()));
    (db, item, client, store, path)
}
fn patch(length: u64) -> Patch {
    Patch {
        message_id: "source-0@fixture".into(),
        geometry: Geometry {
            offset: 0,
            file_size: length,
            part: Some(1),
            total: Some(u32::try_from(length / 8).expect("parts")),
        },
        proof: Proof {
            set: [1; 16],
            file: [2; 16],
            file_md5: [3; 16],
            file_length: length,
        },
        data: Bytes::from_static(b"restored"),
    }
}
fn health(db: Db, client: Arc<Client>) -> Health {
    Health::new(
        db,
        client,
        watch::channel(vec![]).1,
        watch::channel(Settings {
            media_probe: false,
            unowned_action: UnownedAction::ActionNeeded,
            ..Settings::default()
        })
        .1,
    )
    .expect("health")
}
#[tokio::test]
async fn repaired_recorded_damage_is_rechecked_and_never_sent_to_unowned_replacement() {
    let (db, item, client, store, path) = setup(1).await;
    store.publish(vec![patch(8)]).await.expect("publish");
    let health = health(db.clone(), client);
    assert!(health.step().await.expect("observe"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Unchecked
    );
    assert!(health.step().await.expect("fresh check"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    drop(health);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn uncertain_publication_defers_replacement_and_uncovered_holes_do_not_look_repaired() {
    let (db, item, client, store, path) = setup(1).await;
    let fail = Arc::new(AtomicBool::new(true));
    assert!(store.set_witness(Arc::new({
        let fail = fail.clone();
        move |_, _| {
            let fail = fail.clone();
            Box::pin(async move {
                if fail.load(Ordering::SeqCst) {
                    Err(Error::Witness)
                } else {
                    Ok(())
                }
            })
        }
    })));
    assert!(store.publish(vec![patch(8)]).await.is_err());
    let h = health(db.clone(), client);
    assert!(h.step().await.expect("defer"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Repairing
    );
    assert!(db.item(item.id).await.expect("item").is_some());
    fail.store(false, Ordering::SeqCst);
    store.reconcile().await.expect("readback");
    drop(h);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
    let (db, item, client, store, path) = setup(2).await;
    store
        .publish(vec![patch(16)])
        .await
        .expect("partial source repair");
    let h = health(db.clone(), client);
    assert!(h.step().await.expect("still needs repair"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::ActionNeeded,
        "one repaired article cannot clear another recorded missing article"
    );
    drop(h);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn publication_queued_before_replacement_is_observed_after_the_shared_barrier() {
    let (db, item, client, store, path) = setup(1).await;
    let h = Arc::new(health(db.clone(), client));
    let gate = h.mutation_gate();
    assert!(store.set_mutation_gate(gate.clone()));
    let old_mutation = gate.clone().read_owned().await;
    let publish = {
        let store = store.clone();
        tokio::spawn(async move { store.publish(vec![patch(8)]).await })
    };
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if gate.try_read().is_err() {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("publication is queued at the barrier");
    assert!(
        !store.is_pending(),
        "do not hold a write transaction or change source state while another mutation runs"
    );
    let check = {
        let h = h.clone();
        tokio::spawn(async move { h.step().await })
    };
    drop(old_mutation);
    publish.await.expect("publication task").expect("publish");
    assert!(check.await.expect("health task").expect("health"));
    let (_, mut status) = db.health(item.id).await.expect("health").expect("file");
    assert!(matches!(
        status.state,
        HealthState::Repairing | HealthState::Unchecked
    ));
    assert!(
        db.item(item.id).await.expect("item").is_some(),
        "publication must prevent source deletion"
    );
    if status.state == HealthState::Repairing {
        // The initial observation may see the publication before activation and
        // defer for five seconds. Make that retry due without a wall-clock sleep.
        status.next_check = Some(0);
        db.set_health(item.id, &status, status.recheck)
            .await
            .expect("pending retry due");
        assert!(h.step().await.expect("observe committed publication"));
        assert_eq!(
            db.health(item.id)
                .await
                .expect("health")
                .expect("file")
                .1
                .state,
            HealthState::Unchecked
        );
    }
    assert!(h.step().await.expect("validate"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    drop(h);
    drop(gate);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn disabling_new_repairs_does_not_skip_readback_of_previously_committed_data() {
    let (db, item, client, store, path) = setup(1).await;
    store
        .publish(vec![patch(8)])
        .await
        .expect("previous repair");
    let h = Health::new(
        db.clone(),
        client,
        watch::channel(vec![]).1,
        watch::channel(Settings {
            auto_repair: false,
            media_probe: false,
            ..Settings::default()
        })
        .1,
    )
    .expect("health");
    assert!(h.step().await.expect("readback"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Unchecked
    );
    assert!(h.step().await.expect("fresh check"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    drop(h);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn confirmed_absence_after_patch_eviction_does_not_loop_through_the_same_readback_after_restart()
 {
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::LoopbackServer;
    use std::time::Duration;
    let (db, item, client, store, path) = setup(1).await;
    store.publish(vec![patch(8)]).await.expect("repair");
    store
        .evict_payload("source-0@fixture")
        .await
        .expect("retention");
    let server = LoopbackServer::start([]).await.expect("server");
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
            max_connections: 1,
            min_connections: 0,
            pipeline_depth: 1,
            idle_timeout: Duration::from_secs(5),
        },
        Downloads::new(2),
    )
    .expect("pool");
    client.replace([Provider {
        pool,
        key: "fixture".into(),
        name: "fixture".into(),
        priority: 0,
        backbone: None,
    }]);
    let h = health(db.clone(), client.clone());
    assert!(h.step().await.expect("readback admission"));
    assert!(h.step().await.expect("fresh confirmed provider miss"));
    let (_, mut status) = db.health(item.id).await.expect("health").expect("file");
    assert_eq!(status.state, HealthState::Suspect);
    status.suspect_since = Some(0);
    status.next_check = Some(0);
    db.set_health(item.id, &status, status.recheck)
        .await
        .expect("confirmation due");
    assert!(h.step().await.expect("second provider miss"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Repairing
    );
    drop(h);
    let h = health(db.clone(), client);
    assert!(h.step().await.expect("replacement policy after restart"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::ActionNeeded,
        "same repair revision cannot keep resetting confirmed absence"
    );
    let mut newer = patch(8);
    newer.data = Bytes::from_static(b"newbytes");
    store
        .publish(vec![newer])
        .await
        .expect("new repair revision");
    let (_, mut status) = db.health(item.id).await.expect("health").expect("file");
    status.state = HealthState::Repairing;
    status.next_check = Some(0);
    db.set_health(item.id, &status, status.recheck)
        .await
        .expect("retry");
    assert!(
        h.step()
            .await
            .expect("new revision needs independent readback")
    );
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Unchecked
    );
    assert!(h.step().await.expect("new revision healthy"));
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Healthy
    );
    drop(h);
    drop(store);
    drop(db);
    std::fs::remove_dir_all(path).expect("cleanup");
}
