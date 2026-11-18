use super::*;
use crate::{Cache, Settings, legacy::Maintenance};
use std::time::Duration;

async fn cache(f: &Fixture, recovery_mode: bool) -> Arc<Cache> {
    Cache::open(Settings {
        metadata_path: f.path.join("rust-metadata").to_string_lossy().into_owned(),
        min_file_mb: 0,
        recovery_mode,
        ..Settings::default()
    })
    .await
    .unwrap()
}
async fn maintain(writer: &Arc<Writer>, cache: &Arc<Cache>) {
    writer
        .maintain(Maintenance {
            cache: cache.clone(),
        })
        .await
        .unwrap();
}
async fn interrupted(f: &Fixture, chunk: u32, block: u64) -> Arc<Writer> {
    let mut writer = f.open(chunk).await;
    assert!(put(&writer, 0).await.unwrap());
    fault(&mut writer, Fault::Error("before_catalogue", 5));
    assert!(put(&writer, block).await.is_err());
    assert!(accounting(&writer.legacy).await.1 > 0);
    writer.legacy.db.close().await;
    drop(writer);
    f.open(chunk).await
}
async fn entry(writer: &Writer) -> Entry {
    writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap()
}
fn directory(f: &Fixture, entry: &Entry) -> PathBuf {
    f.path
        .join(&entry.folder)
        .join("v1")
        .join(&entry.key[..2])
        .join(&entry.key)
}

#[tokio::test]
async fn maintenance_recovers_after_restart_without_rereading_the_pending_offset() {
    for chunk in [0, 8] {
        for block in [1, 3] {
            let f = Fixture::new().await;
            let writer = interrupted(&f, chunk, block).await;
            let cache = cache(&f, false).await;
            let before = accounting(&writer.legacy).await;
            assert!(writer.legacy.coverage(&entry(&writer).await).await.is_err());
            // Eviction is disabled. Recovery must still run and preserve pins.
            sqlx::query("UPDATE Entries SET Pinned=1")
                .execute(&writer.legacy.db)
                .await
                .unwrap();
            maintain(&writer, &cache).await;
            let after = accounting(&writer.legacy).await;
            assert_eq!(after.0, before.0);
            assert_eq!(after.1, 0);
            assert_eq!(after.2, (BLOCK + bytes(block).len() as u64).cast_signed());
            assert_eq!(entry(&writer).await.pinned, 1);
            let dirty: i64 = sqlx::query_scalar("SELECT Dirty FROM Entries")
                .fetch_one(&writer.legacy.db)
                .await
                .unwrap();
            assert_eq!(dirty, 0);
            assert!(
                !writer
                    .legacy
                    .coverage(&entry(&writer).await)
                    .await
                    .unwrap()
                    .is_empty()
            );
            assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
            assert_eq!(read(&writer.legacy, block).await.unwrap(), bytes(block));
            maintain(&writer, &cache).await;
            assert_eq!(accounting(&writer.legacy).await, after);
            assert!(put(&writer, 2).await.unwrap());
        }
    }
}

#[tokio::test]
async fn unsafe_publications_remain_held_without_touching_committed_bytes() {
    for damage in [
        "payload",
        "missing_payload",
        "partial",
        "conflict",
        "bounds",
        "manifest",
        "volume",
        "missing_journal",
        "unexplained",
        "identity",
        "retired",
    ] {
        let f = Fixture::new().await;
        let writer = interrupted(&f, 8, 3).await;
        let cache = cache(&f, false).await;
        let e = entry(&writer).await;
        let dir = directory(&f, &e);
        let committed_payload = dir.join(payload_name(&e, 0));
        let committed_before = std::fs::read(&committed_payload).unwrap();
        let journal = dir.join("ranges.journal");
        let payload = dir.join(payload_name(&e, 3 * BLOCK));
        match damage {
            "payload" => {
                use std::os::unix::fs::FileExt as _;
                std::fs::OpenOptions::new()
                    .write(true)
                    .open(payload)
                    .unwrap()
                    .write_all_at(b"invalid", 3 * BLOCK % e.chunk.cast_unsigned())
                    .unwrap();
            }
            "missing_payload" => std::fs::remove_file(payload).unwrap(),
            "missing_journal" => std::fs::remove_file(&journal).unwrap(),
            "partial" => {
                use std::io::Write as _;
                std::fs::OpenOptions::new()
                    .append(true)
                    .open(&journal)
                    .unwrap()
                    .write_all(b"{\"Offset\":")
                    .unwrap();
            }
            "conflict" | "bounds" => {
                use std::io::Write as _;
                let offset = if damage == "bounds" { SIZE } else { 3 * BLOCK };
                let record = serde_json::json!({"Offset":offset,"Count":37,"Hash":"00".repeat(32)});
                writeln!(
                    std::fs::OpenOptions::new()
                        .append(true)
                        .open(&journal)
                        .unwrap(),
                    "{record}"
                )
                .unwrap();
            }
            "manifest" => std::fs::write(dir.join("manifest.json"), b"{}").unwrap(),
            "volume" => std::fs::write(
                f.path.join(&e.folder).join(".infinidysk-volume"),
                Uuid::new_v4().simple().to_string(),
            )
            .unwrap(),
            "unexplained" => {
                sqlx::query("UPDATE Entries SET PendingBytes=1")
                    .execute(&writer.legacy.db)
                    .await
                    .unwrap();
            }
            "identity" => {
                sqlx::query("UPDATE Entries SET Generation='rust-v2:changed-revision'")
                    .execute(&writer.legacy.db)
                    .await
                    .unwrap();
            }
            "retired" => {
                sqlx::query("INSERT INTO RetiredEntries(Key,Folder,Bytes) VALUES($1,$2,0)")
                    .bind(&e.key)
                    .bind(&e.folder)
                    .execute(&writer.legacy.db)
                    .await
                    .unwrap();
            }
            _ => unreachable!(),
        }
        let before = accounting(&writer.legacy).await;
        maintain(&writer, &cache).await;
        maintain(&writer, &cache).await;
        assert_eq!(accounting(&writer.legacy).await, before, "{damage}");
        let blocks: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Blocks")
            .fetch_one(&writer.legacy.db)
            .await
            .unwrap();
        assert_eq!(blocks, 1, "{damage}");
        assert_eq!(
            std::fs::read(&committed_payload).unwrap(),
            committed_before,
            "{damage}"
        );
    }
}

#[tokio::test]
async fn bounded_batches_rotate_past_held_entries() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    fault(&mut writer, Fault::Error("before_catalogue", 5));
    for value in 1..=6 {
        assert!(
            writer
                .put(
                    Uuid::from_u128(value),
                    GENERATION.into(),
                    SIZE,
                    0,
                    bytes(0),
                    Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
                )
                .await
                .is_err()
        );
    }
    let bad = writer
        .legacy
        .find(Uuid::from_u128(1), GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    std::fs::write(directory(&f, &bad).join("ranges.journal"), b"partial").unwrap();
    writer.legacy.db.close().await;
    drop(writer);
    let writer = f.open(8).await;
    let cache = cache(&f, false).await;
    maintain(&writer, &cache).await;
    let published: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Blocks")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert!((1..=4).contains(&published));
    for _ in 0..4 {
        maintain(&writer, &cache).await;
    }
    let published: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Blocks")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    let held: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Entries WHERE PendingBytes>0")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert_eq!((published, held), (5, 1));
}

#[tokio::test]
async fn recovery_mode_leaves_pending_publications_untouched() {
    let f = Fixture::new().await;
    let writer = interrupted(&f, 8, 1).await;
    let cache = cache(&f, true).await;
    let before = accounting(&writer.legacy).await;
    maintain(&writer, &cache).await;
    assert_eq!(accounting(&writer.legacy).await, before);
}

#[tokio::test]
async fn process_death_recovers_through_maintenance_without_a_media_read() {
    use std::os::unix::process::ExitStatusExt as _;
    let f = Fixture::new().await;
    let writer = f.open(8).await;
    assert!(put(&writer, 0).await.unwrap());
    writer.legacy.db.close().await;
    drop(writer);
    let output = std::process::Command::new("bash")
        .args(["-c", "ulimit -c 0; exec \"$@\"", "_"])
        .arg(std::env::current_exe().unwrap())
        .args(["--ignored", "--exact", "legacy::writer::tests::crash_child"])
        .env("ID_COMPATIBLE_CACHE_CHILD_ROOT", &f.path)
        .env("ID_COMPATIBLE_CACHE_CHILD_STAGE", "before_catalogue")
        .output()
        .unwrap();
    assert_eq!(output.status.signal(), Some(6));
    let writer = f.open(8).await;
    assert!(accounting(&writer.legacy).await.1 > 0);
    let cache = cache(&f, false).await;
    maintain(&writer, &cache).await;
    assert_eq!(accounting(&writer.legacy).await.1, 0);
    assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
    assert_eq!(read(&writer.legacy, 1).await.unwrap(), bytes(1));
    assert!(put(&writer, 2).await.unwrap());
}

fn release(gate: &Arc<(Mutex<bool>, std::sync::Condvar)>) {
    let (lock, wake) = &**gate;
    *lock.lock().unwrap() = true;
    wake.notify_all();
}
async fn entered(flag: &std::sync::atomic::AtomicBool) {
    for _ in 0..200 {
        if flag.load(Ordering::Acquire) {
            return;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("storage operation did not reach gate");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn maintenance_skips_active_publication_and_survives_caller_cancellation() {
    let f = Fixture::new().await;
    let cache = cache(&f, false).await;
    let mut writer = f.open(8).await;
    let flag = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let gate = Arc::new((Mutex::new(false), std::sync::Condvar::new()));
    fault(
        &mut writer,
        Fault::Gate("before_write", flag.clone(), gate.clone()),
    );
    let publish = tokio::spawn({
        let writer = writer.clone();
        async move { put(&writer, 0).await }
    });
    entered(&flag).await;
    let before = accounting(&writer.legacy).await;
    let result = tokio::time::timeout(
        Duration::from_secs(1),
        writer.maintain(Maintenance {
            cache: cache.clone(),
        }),
    )
    .await;
    // Always release the blocking gate before asserting, including on failure.
    release(&gate);
    result.unwrap().unwrap();
    assert!(before.1 > 0);
    assert!(publish.await.unwrap().unwrap());
    writer.legacy.db.close().await;
    drop(writer);

    // A separate pending record enters recovery's durable commit path.
    let mut writer = f.open(8).await;
    fault(&mut writer, Fault::Error("before_catalogue", 5));
    assert!(put(&writer, 1).await.is_err());
    writer.legacy.db.close().await;
    drop(writer);
    let mut writer = f.open(8).await;
    let flag = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let gate = Arc::new((Mutex::new(false), std::sync::Condvar::new()));
    fault(
        &mut writer,
        Fault::Gate("before_catalogue", flag.clone(), gate.clone()),
    );
    let request = tokio::spawn({
        let writer = writer.clone();
        let cache = cache.clone();
        async move { maintain(&writer, &cache).await }
    });
    entered(&flag).await;
    request.abort();
    let _ = request.await;
    // A second maintenance call cannot start overlapping recovery.
    maintain(&writer, &cache).await;
    assert!(accounting(&writer.legacy).await.1 > 0);
    release(&gate);
    for _ in 0..200 {
        if accounting(&writer.legacy).await.1 == 0 {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert_eq!(accounting(&writer.legacy).await.1, 0);
    assert_eq!(read(&writer.legacy, 1).await.unwrap(), bytes(1));
}
