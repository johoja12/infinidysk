//! Durable publication, rollback detection and payload-independent repair revisions.
use bytes::Bytes;
use infinidysk_native_cache::repair::{Error, Geometry, Patch, Proof, Revision, Store};
use infinidysk_native_cache::{Cache, Settings};
use sqlx::sqlite::{SqliteConnectOptions, SqlitePoolOptions};
use std::path::{Path, PathBuf};
use uuid::Uuid;
fn directory() -> PathBuf {
    let path = std::env::temp_dir().join(format!("infinidysk-repair-ledger-{}", Uuid::new_v4()));
    std::fs::create_dir(&path).expect("directory");
    path
}
fn patch(id: &str, data: &'static [u8]) -> Patch {
    Patch {
        message_id: id.into(),
        geometry: Geometry {
            offset: 8,
            file_size: 100,
            part: Some(2),
            total: Some(4),
        },
        proof: Proof {
            set: [1; 16],
            file: [2; 16],
            file_md5: [3; 16],
            file_length: 100,
        },
        data: Bytes::from_static(data),
    }
}
fn revision(store: &Store, id: &str) -> [u8; 32] {
    match store.revision(id) {
        Revision::Repaired(r) => r,
        other => panic!("expected repair, found {other:?}"),
    }
}
async fn database(path: &Path) -> sqlx::SqlitePool {
    SqlitePoolOptions::new()
        .max_connections(1)
        .connect_with(SqliteConnectOptions::new().filename(path.join("catalogue.sqlite")))
        .await
        .expect("database")
}
#[tokio::test]
async fn publication_is_persistent_and_payload_eviction_never_forgets_a_revision() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    assert_eq!(store.revision("a@fixture"), Revision::Base);
    store
        .publish(vec![
            patch("a@fixture", b"restored"),
            patch("b@fixture", b"other"),
        ])
        .await
        .expect("publish");
    let id = store.id();
    let a = revision(&store, "a@fixture");
    let b = revision(&store, "b@fixture");
    assert_eq!(store.epoch(), 1);
    assert!(store.generation() >= 2);
    assert_eq!(
        store
            .get("a@fixture", a)
            .await
            .expect("verified bytes")
            .data
            .as_ref(),
        b"restored"
    );
    assert!(store.evict_payload("a@fixture").await.expect("evict"));
    assert_eq!(revision(&store, "a@fixture"), a);
    assert!(matches!(
        store.get("a@fixture", a).await,
        Err(Error::PayloadMissing)
    ));
    drop(store);
    let store = Store::open(&path).await.expect("restart");
    assert_eq!(store.id(), id);
    assert_eq!(revision(&store, "a@fixture"), a);
    assert!(matches!(
        store.get("a@fixture", a).await,
        Err(Error::PayloadMissing)
    ));
    assert_eq!(
        store
            .get("b@fixture", b)
            .await
            .expect("other bytes")
            .data
            .as_ref(),
        b"other"
    );
    store
        .publish(vec![patch("a@fixture", b"restored")])
        .await
        .expect("restore same repair");
    assert_eq!(revision(&store, "a@fixture"), a);
    store
        .publish(vec![patch("a@fixture", b"changed")])
        .await
        .expect("new repair");
    assert_ne!(revision(&store, "a@fixture"), a);
    assert_eq!(revision(&store, "b@fixture"), b);
    assert_eq!(store.revision("untouched@fixture"), Revision::Base);
    assert!(matches!(
        store.get("a@fixture", a).await,
        Err(Error::Changed)
    ));
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn rollback_or_lost_catalogue_cannot_reset_repairs_to_base() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    store
        .publish(vec![patch("a@fixture", b"restored")])
        .await
        .expect("publish");
    drop(store);
    let db = database(&path).await;
    sqlx::query("UPDATE repair_state SET epoch=0")
        .execute(&db)
        .await
        .expect("simulate stale restore");
    db.close().await;
    assert!(matches!(Store::open(&path).await, Err(Error::Invalid(_))));
    std::fs::remove_file(path.join("catalogue.sqlite")).expect("simulate missing catalogue");
    assert!(Store::open(&path).await.is_err());
    assert!(
        !path.join("catalogue.sqlite").exists(),
        "do not replace lost state with an empty ledger"
    );
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn corruption_and_descriptor_changes_are_rejected_and_writer_ownership_is_exclusive() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    assert!(Store::open(&path).await.is_err());
    store
        .publish(vec![patch("a@fixture", b"restored")])
        .await
        .expect("publish");
    let rev = revision(&store, "a@fixture");
    let db = database(&path).await;
    sqlx::query("UPDATE article_repairs SET payload=$1")
        .bind(b"corrupt!".as_slice())
        .execute(&db)
        .await
        .expect("corrupt bytes");
    assert!(matches!(
        store.get("a@fixture", rev).await,
        Err(Error::Invalid(_))
    ));
    sqlx::query("UPDATE article_repairs SET geometry=$1")
        .bind(r#"{"offset":9,"file_size":100,"part":2,"total":4}"#)
        .execute(&db)
        .await
        .expect("corrupt geometry");
    db.close().await;
    drop(store);
    assert!(matches!(Store::open(&path).await, Err(Error::Invalid(_))));
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn failed_atomic_batch_stays_pending_until_readback_without_replaying_any_patch() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    store
        .publish(vec![patch("existing@fixture", b"retained")])
        .await
        .expect("publish");
    let retained = revision(&store, "existing@fixture");
    let db = database(&path).await;
    sqlx::query("CREATE TRIGGER reject_repair BEFORE INSERT ON article_repairs BEGIN SELECT RAISE(ABORT,'fixture storage rejection'); END").execute(&db).await.expect("inject write failure");
    assert!(
        store
            .publish(vec![
                patch("a@fixture", b"first"),
                patch("b@fixture", b"second")
            ])
            .await
            .is_err()
    );
    assert_eq!(store.revision("a@fixture"), Revision::Pending);
    assert_eq!(store.revision("b@fixture"), Revision::Pending);
    assert_eq!(revision(&store, "existing@fixture"), retained);
    assert!(matches!(
        store.publish(vec![patch("c@fixture", b"third")]).await,
        Err(Error::Pending)
    ));
    store
        .reconcile()
        .await
        .expect("observe rollback, not retry writes");
    assert_eq!(store.revision("a@fixture"), Revision::Base);
    assert_eq!(store.revision("b@fixture"), Revision::Base);
    let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM article_repairs")
        .fetch_one(&db)
        .await
        .expect("count");
    assert_eq!(count, 1);
    sqlx::query("DROP TRIGGER reject_repair")
        .execute(&db)
        .await
        .expect("remove injection");
    store
        .publish(vec![patch("a@fixture", b"first")])
        .await
        .expect("explicit new attempt");
    db.close().await;
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn native_cache_and_repair_ledger_share_metadata_without_an_extra_payload_cache() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    let cache = Cache::open(Settings {
        metadata_path: path.to_str().expect("path").into(),
        ..Settings::default()
    })
    .await
    .expect("cache without payload folders");
    store
        .publish(vec![patch("a@fixture", b"restored")])
        .await
        .expect("publish");
    drop(cache);
    drop(store);
    let store = Store::open(&path).await.expect("reopen shared catalogue");
    assert_eq!(store.epoch(), 1);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn invalid_batches_have_no_publication_side_effects() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    assert!(
        store
            .publish(vec![patch("a@fixture", b"one"), patch("a@fixture", b"two")])
            .await
            .is_err()
    );
    assert!(
        store
            .publish(vec![patch("bad\r\nid", b"data")])
            .await
            .is_err()
    );
    let mut bad = patch("geometry@fixture", b"data");
    bad.geometry.offset = u64::MAX;
    assert!(store.publish(vec![bad]).await.is_err());
    assert_eq!(store.revision("a@fixture"), Revision::Base);
    assert_eq!(store.epoch(), 0);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn an_anchor_failure_after_commit_is_reconciled_by_readback_not_republication() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    store
        .publish(vec![
            patch("a@fixture", b"before"),
            patch("other@fixture", b"stable"),
        ])
        .await
        .expect("initial repair");
    let old = revision(&store, "a@fixture");
    let other = revision(&store, "other@fixture");
    std::fs::remove_file(path.join("repair-anchor.json")).expect("anchor");
    std::fs::create_dir(path.join("repair-anchor.json")).expect("inject rename failure");
    assert!(
        store
            .publish(vec![
                patch("a@fixture", b"after"),
                patch("new@fixture", b"new")
            ])
            .await
            .is_err()
    );
    assert_eq!(store.revision("a@fixture"), Revision::Pending);
    assert_eq!(store.revision("new@fixture"), Revision::Pending);
    assert_eq!(
        store
            .get("other@fixture", other)
            .await
            .expect("unaffected source")
            .data
            .as_ref(),
        b"stable"
    );
    assert!(matches!(
        store.get("a@fixture", old).await,
        Err(Error::Pending)
    ));
    let temporary = std::fs::read_dir(&path)
        .expect("directory")
        .filter_map(Result::ok)
        .filter(|e| {
            e.file_name()
                .to_string_lossy()
                .starts_with(".repair-anchor-")
        })
        .count();
    assert_eq!(
        temporary, 0,
        "failed atomic anchor write cleans up its temporary file"
    );
    std::fs::remove_dir(path.join("repair-anchor.json")).expect("restore anchor destination");
    store
        .reconcile()
        .await
        .expect("observe committed patch without rewriting it");
    assert_eq!(store.epoch(), 2);
    let new = revision(&store, "a@fixture");
    assert_ne!(new, old);
    assert_eq!(
        store
            .get("a@fixture", new)
            .await
            .expect("new bytes")
            .data
            .as_ref(),
        b"after"
    );
    drop(store);
    let store = Store::open(&path).await.expect("restart committed repair");
    assert_eq!(store.epoch(), 2);
    assert_eq!(revision(&store, "a@fixture"), new);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
#[tokio::test]
async fn symlinked_anchors_and_replaced_metadata_directories_cannot_redirect_state() {
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    store
        .publish(vec![patch("a@fixture", b"retained")])
        .await
        .expect("publish");
    let rev = revision(&store, "a@fixture");
    let moved = path.with_extension("moved");
    std::fs::rename(&path, &moved).expect("simulate mount change");
    std::fs::create_dir(&path).expect("replacement path");
    assert!(store.get("a@fixture", rev).await.is_err());
    assert!(
        store
            .publish(vec![patch("b@fixture", b"rejected")])
            .await
            .is_err()
    );
    assert!(
        std::fs::read_dir(&path)
            .expect("replacement directory")
            .next()
            .is_none()
    );
    drop(store);
    std::fs::remove_dir(&path).expect("remove replacement");
    std::fs::rename(&moved, &path).expect("restore mount");
    let anchor = std::fs::read(path.join("repair-anchor.json")).expect("anchor");
    std::fs::write(path.join("outside.json"), &anchor).expect("outside");
    std::fs::remove_file(path.join("repair-anchor.json")).expect("remove anchor");
    std::os::unix::fs::symlink(path.join("outside.json"), path.join("repair-anchor.json"))
        .expect("symlink");
    assert!(Store::open(&path).await.is_err());
    assert_eq!(
        std::fs::read(path.join("outside.json")).expect("outside unchanged"),
        anchor
    );
    std::fs::remove_dir_all(path).expect("cleanup");
}

#[tokio::test]
async fn the_actual_ledger_serves_body_and_stat_without_providers_and_checks_geometry() {
    use infinidysk_nntp::{BodyExpectation, Client, Workload};
    let path = directory();
    let store = Store::open(&path).await.expect("store");
    let client = Client::new([]);
    assert!(client.set_repairs(store.clone()));
    store
        .publish(vec![patch("a@fixture", b"restored")])
        .await
        .expect("repair");
    let body = client
        .body_checked(
            "a@fixture",
            Workload::Health,
            BodyExpectation {
                offset: Some(8),
                decoded_len: Some(8),
                file_size: Some(100),
                ..BodyExpectation::default()
            },
        )
        .await
        .expect("body without providers");
    assert_eq!(body.data.as_ref(), b"restored");
    assert_eq!(body.part, Some(2));
    client
        .stat("a@fixture", Workload::Health, None)
        .await
        .expect("STAT against verified repair");
    assert!(
        client
            .body_with("a@fixture", Workload::Stream, None, None, Some(9))
            .await
            .is_err()
    );
    store
        .evict_payload("a@fixture")
        .await
        .expect("payload retention");
    assert!(matches!(
        client.stat("a@fixture", Workload::Health, None).await,
        Err(infinidysk_nntp::Error::Unavailable)
    ));
    drop(client);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}
