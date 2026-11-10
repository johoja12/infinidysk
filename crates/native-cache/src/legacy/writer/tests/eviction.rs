use super::*;
use crate::legacy::Maintenance;
use crate::{Cache, Key, Settings};
use std::time::Duration;

async fn open_cache(f: &Fixture) -> Arc<Cache> {
    Cache::open(Settings {
        metadata_path: f.path.join("rust-metadata").to_string_lossy().into_owned(),
        min_file_mb: 0,
        ..Settings::default()
    })
    .await
    .unwrap()
}
fn enable(writer: &mut Arc<Writer>) {
    for folder in &mut Arc::get_mut(writer).unwrap().settings.folders {
        folder.eviction = Some(EvictionPolicy {
            max_age_days: 1,
            high_percent: 90,
            low_percent: 80,
        });
    }
}
async fn age(writer: &Writer) {
    writer.legacy.flush_recency().await.unwrap();
    sqlx::query("UPDATE Entries SET Access=0")
        .execute(&writer.legacy.db)
        .await
        .unwrap();
}
async fn maintain(writer: &Arc<Writer>, cache: &Arc<Cache>) -> Result<(), Error> {
    writer
        .maintain(Maintenance {
            cache: cache.clone(),
        })
        .await
}
async fn counts(writer: &Writer, cache: &Cache) -> (i64, i64, i64) {
    let entries = sqlx::query_scalar("SELECT COUNT(*) FROM Entries")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    let retired = sqlx::query_scalar("SELECT COUNT(*) FROM RetiredEntries")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    let intents = sqlx::query_scalar("SELECT COUNT(*) FROM legacy_evictions_v1")
        .fetch_one(&cache.db)
        .await
        .unwrap();
    (entries, retired, intents)
}
async fn another(writer: &Arc<Writer>, item: Uuid) {
    assert!(
        writer
            .put(
                item,
                GENERATION.into(),
                SIZE,
                0,
                bytes(0),
                Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
            )
            .await
            .unwrap()
    );
}

#[tokio::test]
async fn policy_is_opt_in_and_preserves_pins_pending_writes_and_whole_playback_leases() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    enable(&mut writer);
    for pinned in [1, 2] {
        sqlx::query("UPDATE Entries SET Pinned=$1")
            .bind(pinned)
            .execute(&writer.legacy.db)
            .await
            .unwrap();
        maintain(&writer, &cache).await.unwrap();
        assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    }
    sqlx::query("UPDATE Entries SET Pinned=0,PendingBytes=1,Dirty=1")
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    sqlx::query("UPDATE Entries SET PendingBytes=0,Dirty=0")
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    let direct = writer.legacy.entry_lock(&entry.key).read_owned().await;
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    drop(direct);
    let lease = cache.lease(Key::new(ITEM, b"whole-session", SIZE)).unwrap();
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    drop(lease);
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (0, 0, 0));
    assert_eq!(allocated(&writer).await, 0);
    assert_eq!(cache.evictions(None, 100).await.unwrap().len(), 1);
}

#[tokio::test]
async fn verified_reads_refresh_original_access_and_age_eviction_skips_recent_hits() {
    let f = Fixture::new().await;
    let mut writer = f.open(0).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
    writer.legacy.flush_recency().await.unwrap();
    let access: i64 = sqlx::query_scalar("SELECT Access FROM Entries")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert!(access > 0);
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    age(&writer).await;
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (0, 0, 0));
}

#[tokio::test]
async fn pressure_reclaims_oldest_entries_to_low_watermark_without_relocation() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    let cache = open_cache(&f).await;
    let items = [
        Uuid::from_u128(42),
        Uuid::from_u128(43),
        Uuid::from_u128(44),
    ];
    for (index, item) in items.into_iter().enumerate() {
        another(&writer, item).await;
        sqlx::query("UPDATE Entries SET Access=$2 WHERE ItemId=$1")
            .bind(item.simple().to_string())
            .bind(i64::try_from(index).unwrap())
            .execute(&writer.legacy.db)
            .await
            .unwrap();
    }
    let total = allocated(&writer).await;
    let settings = &mut Arc::get_mut(&mut writer).unwrap().settings;
    settings.folders[0].quota_bytes = total * 4 / 3;
    settings.folders[0].eviction = Some(EvictionPolicy {
        max_age_days: 0,
        high_percent: 70,
        low_percent: 40,
    });
    maintain(&writer, &cache).await.unwrap();
    assert!(
        writer
            .legacy
            .find(items[0], GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        writer
            .legacy
            .find(items[1], GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        writer
            .legacy
            .find(items[2], GENERATION, SIZE)
            .await
            .unwrap()
            .is_some()
    );
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
}

#[tokio::test]
async fn interrupted_deletion_resumes_without_early_capacity_credit_or_damage_to_other_entries() {
    for stage in [
        "eviction_intent",
        "eviction_staged",
        "eviction_renamed",
        "eviction_unlinked",
        "eviction_manifest_removed",
        "eviction_removed",
        "eviction_metadata",
    ] {
        let f = Fixture::new().await;
        let mut writer = f.open(8).await;
        enable(&mut writer);
        let cache = open_cache(&f).await;
        put(&writer, 0).await.unwrap();
        age(&writer).await;
        let other = Uuid::from_u128(43);
        another(&writer, other).await;
        let before = allocated(&writer).await;
        fault(&mut writer, Fault::Error(stage, 5));
        assert!(maintain(&writer, &cache).await.is_err(), "{stage}");
        if stage != "eviction_metadata" {
            assert_eq!(allocated(&writer).await, before, "{stage}");
        }
        writer.legacy.db.close().await;
        drop(writer);
        let mut writer = f.open(8).await;
        enable(&mut writer);
        maintain(&writer, &cache).await.unwrap();
        assert_eq!(counts(&writer, &cache).await, (1, 0, 0), "{stage}");
        let entry = writer
            .legacy
            .find(other, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            writer
                .legacy
                .read_block(
                    &entry,
                    0,
                    Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
                )
                .await
                .unwrap()
                .unwrap(),
            bytes(0)
        );
        assert_eq!(
            cache.evictions(None, 100).await.unwrap().len(),
            1,
            "{stage}"
        );
    }
}

#[tokio::test]
async fn unavailable_storage_and_unknown_or_linked_leaves_hold_deletion() {
    for linked in [false, true] {
        let f = Fixture::new().await;
        let mut writer = f.open(0).await;
        enable(&mut writer);
        let cache = open_cache(&f).await;
        put(&writer, 0).await.unwrap();
        age(&writer).await;
        let entry = writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap();
        let directory = f.path.join("a/v1").join(&entry.key[..2]).join(&entry.key);
        let unrelated = f.path.join("unrelated");
        std::fs::write(&unrelated, b"untouched").unwrap();
        if linked {
            symlink(&unrelated, directory.join("unexpected")).unwrap();
        } else {
            std::fs::write(directory.join("unexpected"), b"untouched").unwrap();
        }
        assert!(maintain(&writer, &cache).await.is_err());
        assert_eq!(std::fs::read(&unrelated).unwrap(), b"untouched");
        assert_eq!(counts(&writer, &cache).await, (0, 1, 1));
        assert!(allocated(&writer).await > 0);
    }
    let f = Fixture::new().await;
    let mut writer = f.open(0).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    std::fs::write(f.path.join("a/.infinidysk-volume"), b"changed").unwrap();
    assert!(maintain(&writer, &cache).await.is_err());
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn caller_cancellation_keeps_admission_and_original_ownership_until_delete_finishes() {
    use nix::fcntl::{Flock, FlockArg};
    struct Release(Arc<(Mutex<bool>, std::sync::Condvar)>);
    impl Drop for Release {
        fn drop(&mut self) {
            *self.0.0.lock().unwrap() = true;
            self.0.1.notify_all();
        }
    }
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    let entered = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let gate = Arc::new((Mutex::new(false), std::sync::Condvar::new()));
    let release = Release(gate.clone());
    fault(
        &mut writer,
        Fault::Gate("eviction_renamed", entered.clone(), gate),
    );
    let request = tokio::spawn({
        let writer = writer.clone();
        let cache = cache.clone();
        async move { maintain(&writer, &cache).await }
    });
    tokio::time::timeout(Duration::from_secs(5), async {
        while !entered.load(Ordering::Acquire) {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    request.abort();
    let _ = request.await;
    drop(writer);
    let key = Key::new(ITEM, b"whole-session", SIZE);
    assert!(cache.lease(key.clone()).is_none());
    let owner = f.path.join("a/.infinidysk-owner");
    assert!(Flock::lock(File::open(&owner).unwrap(), FlockArg::LockExclusiveNonblock).is_err());
    drop(release);
    tokio::time::timeout(Duration::from_secs(10), async {
        loop {
            if let Ok(owner) =
                Flock::lock(File::open(&owner).unwrap(), FlockArg::LockExclusiveNonblock)
            {
                drop(owner);
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    assert!(cache.lease(key).is_some());
    let mut writer = f.open(8).await;
    enable(&mut writer);
    assert_eq!(counts(&writer, &cache).await, (0, 0, 0));
}

async fn allocated(writer: &Writer) -> u64 {
    sqlx::query_scalar::<_, i64>("SELECT COALESCE(SUM(Bytes),0) FROM FolderTotals")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap()
        .cast_unsigned()
}

#[tokio::test]
async fn stale_entry_handles_cannot_invalidate_a_refill_in_another_folder() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    let stale = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(stale.folder, "a");
    age(&writer).await;
    maintain(&writer, &cache).await.unwrap();
    drop(writer);
    let legacy = Arc::new(
        Legacy::open_for_writes(&f.path.join("meta/catalogue.db"), &f.folders)
            .await
            .unwrap(),
    );
    let mut settings = f.settings(8);
    settings.folders[1].priority = 100;
    let writer = Writer::new(legacy, settings).unwrap();
    put(&writer, 0).await.unwrap();
    let current = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(current.folder, "b");
    assert!(
        writer
            .legacy
            .read_block(
                &stale,
                0,
                Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
            )
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
    let blocks: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Blocks")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert_eq!(blocks, 1);
}

#[tokio::test]
async fn lost_recency_holds_eviction_and_free_space_pressure_still_respects_pins() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    writer.legacy.touch(&entry.key);
    for index in 0..4096 {
        writer.legacy.touch(&format!("{}:{index}", entry.key));
    }
    assert!(writer.legacy.flush_recency().await.is_err());
    assert!(maintain(&writer, &cache).await.is_err());
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    writer.legacy.db.close().await;
    drop(writer);
    let mut writer = f.open(8).await;
    enable(&mut writer);
    another(&writer, Uuid::from_u128(43)).await;
    sqlx::query("UPDATE Entries SET Pinned=1 WHERE ItemId=$1")
        .bind(Uuid::from_u128(43).simple().to_string())
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    Arc::get_mut(&mut writer).unwrap().settings.folders[0].min_free_bytes = u64::MAX;
    assert!(maintain(&writer, &cache).await.is_err());
    assert_eq!(counts(&writer, &cache).await, (2, 0, 0));
    let capacity = nix::sys::statvfs::statvfs(f.path.as_path()).unwrap();
    Arc::get_mut(&mut writer).unwrap().settings.folders[0].min_free_bytes =
        crate::filesystem::bytes(capacity.blocks(), capacity.fragment_size()).saturating_sub(1);
    maintain(&writer, &cache).await.unwrap();
    assert!(
        writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
}

#[tokio::test]
async fn victim_cursor_and_folder_failures_do_not_starve_eligible_entries() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    sqlx::query("UPDATE Entries SET Access=100")
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    let mut leases = Vec::new();
    for index in 0..64_u128 {
        let item = Uuid::from_u128(1000 + index);
        let id = item.simple().to_string();
        let encoded = format!("32:{id}{}:{GENERATION}:{SIZE}", GENERATION.len());
        let key = hex(&Sha256::digest(encoded.as_bytes()));
        sqlx::query("INSERT INTO Entries(Key,Folder,Length,Access,ItemId,Generation,ChunkSize) VALUES($1,'a',$2,$3,$4,$5,0)")
            .bind(key).bind(SIZE.cast_signed()).bind(i64::try_from(index).unwrap()).bind(id).bind(GENERATION).execute(&writer.legacy.db).await.unwrap();
        leases.push(cache.lease(Key::new(item, b"active", SIZE)).unwrap());
    }
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (65, 0, 0));
    maintain(&writer, &cache).await.unwrap();
    assert!(
        writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
    drop(leases);

    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    Arc::get_mut(&mut writer).unwrap().settings.folders[0].quota_bytes = 4_500_000;
    let other = Uuid::from_u128(43);
    another(&writer, other).await;
    assert_eq!(
        writer
            .legacy
            .find(other, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap()
            .folder,
        "b"
    );
    age(&writer).await;
    std::fs::write(f.path.join("a/.infinidysk-volume"), b"changed").unwrap();
    assert!(maintain(&writer, &cache).await.is_err());
    assert!(
        writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .is_some()
    );
    assert!(
        writer
            .legacy
            .find(other, GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
}

#[test]
#[ignore = "subprocess helper for compatible eviction crash qualification"]
fn eviction_crash_child() {
    let path = PathBuf::from(std::env::var("ID_EVICTION_FIXTURE").unwrap());
    assert_eq!(
        std::fs::read(path.join("fixture-owner")).unwrap(),
        b"generated-native-cache-write-tests"
    );
    let stage = std::env::var("ID_EVICTION_STAGE").unwrap();
    let stage = match stage.as_str() {
        "eviction_intent" => "eviction_intent",
        "eviction_staged" => "eviction_staged",
        "eviction_renamed" => "eviction_renamed",
        "eviction_unlinked" => "eviction_unlinked",
        "eviction_manifest_removed" => "eviction_manifest_removed",
        "eviction_removed" => "eviction_removed",
        "eviction_metadata" => "eviction_metadata",
        _ => panic!("unknown eviction stage"),
    };
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(async {
            let folders = ["a", "b"]
                .into_iter()
                .map(|id| Folder {
                    id: id.into(),
                    path: path.join(id).to_string_lossy().into_owned(),
                    owner_lock_path: None,
                })
                .collect();
            let f = std::mem::ManuallyDrop::new(Fixture { path, folders });
            let mut writer = f.open(8).await;
            enable(&mut writer);
            let cache = open_cache(&f).await;
            fault(&mut writer, Fault::Crash(stage));
            let _ = maintain(&writer, &cache).await;
            panic!("crash boundary not reached");
        });
}

#[tokio::test]
async fn process_death_replays_only_owned_intents_and_never_resurrects_partial_entries() {
    use std::os::unix::process::ExitStatusExt;
    for stage in [
        "eviction_intent",
        "eviction_staged",
        "eviction_renamed",
        "eviction_unlinked",
        "eviction_manifest_removed",
        "eviction_removed",
        "eviction_metadata",
    ] {
        let f = Fixture::new().await;
        let writer = f.open(8).await;
        let cache = open_cache(&f).await;
        put(&writer, 0).await.unwrap();
        age(&writer).await;
        another(&writer, Uuid::from_u128(43)).await;
        writer.legacy.db.close().await;
        drop(writer);
        cache.db.close().await;
        drop(cache);
        let output = std::process::Command::new("bash")
            .args(["-c", "ulimit -c 0; exec \"$@\"", "_"])
            .arg(std::env::current_exe().unwrap())
            .args([
                "--ignored",
                "--exact",
                "legacy::writer::tests::eviction::eviction_crash_child",
            ])
            .env("ID_EVICTION_FIXTURE", &f.path)
            .env("ID_EVICTION_STAGE", stage)
            .output()
            .unwrap();
        assert_eq!(
            output.status.signal(),
            Some(6),
            "{stage}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let mut writer = f.open(8).await;
        enable(&mut writer);
        let cache = open_cache(&f).await;
        maintain(&writer, &cache).await.unwrap();
        assert_eq!(counts(&writer, &cache).await, (1, 0, 0), "{stage}");
        assert!(
            writer
                .legacy
                .find(ITEM, GENERATION, SIZE)
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            writer
                .legacy
                .find(Uuid::from_u128(43), GENERATION, SIZE)
                .await
                .unwrap()
                .is_some()
        );
        assert_eq!(cache.evictions(None, 100).await.unwrap().len(), 1);
    }
}

#[tokio::test]
#[ignore = "exports owned generated tombstones for original C# scanner interoperability"]
async fn export_eviction_interop_fixtures() {
    let destination = PathBuf::from(std::env::var("ID_EVICTION_EXPORT").unwrap());
    assert!(destination.is_absolute());
    let mut receipts = Vec::new();
    for chunk in [0, 8] {
        let f = std::mem::ManuallyDrop::new(Fixture::new().await);
        let mut writer = f.open(chunk).await;
        enable(&mut writer);
        let cache = open_cache(&f).await;
        put(&writer, 0).await.unwrap();
        age(&writer).await;
        let other = Uuid::from_u128(43);
        another(&writer, other).await;
        fault(&mut writer, Fault::Error("eviction_staged", 5));
        assert!(maintain(&writer, &cache).await.is_err());
        assert_eq!(counts(&writer, &cache).await, (1, 1, 1));
        receipts.push(serde_json::json!({"fixture_root":f.path,"metadata_path":f.path.join("meta"),"folder_path":f.path.join("a"),"folder_id":"a","removed":ITEM.simple().to_string(),"remaining":other.simple().to_string(),"generation":GENERATION,"length":SIZE,"sha256":hex(&Sha256::digest(bytes(0)))}));
        writer.legacy.db.close().await;
        drop(writer);
        cache.db.close().await;
        drop(cache);
    }
    let file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination)
        .unwrap();
    serde_json::to_writer(file, &receipts).unwrap();
}

#[tokio::test]
async fn large_chunked_entries_resume_in_bounded_batches_without_early_credit() {
    let f = Fixture::new().await;
    let mut writer = f.open(4).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    let size = 4097 * BLOCK;
    assert!(
        writer
            .put(
                ITEM,
                GENERATION.into(),
                size,
                0,
                bytes(0),
                Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
            )
            .await
            .unwrap()
    );
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, size)
        .await
        .unwrap()
        .unwrap();
    let dir = f.path.join("a/v1").join(&entry.key[..2]).join(&entry.key);
    for index in 1..4097 {
        std::fs::write(dir.join(format!("chunk-{index:016x}.data")), b"").unwrap();
    }
    age(&writer).await;
    let before = allocated(&writer).await;
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (0, 1, 1));
    assert_eq!(allocated(&writer).await, before);
    maintain(&writer, &cache).await.unwrap();
    assert_eq!(counts(&writer, &cache).await, (0, 0, 0));
    assert_eq!(allocated(&writer).await, 0);
}

#[tokio::test]
async fn restored_current_metadata_does_not_erase_quarantine_ownership_or_delete_new_entries() {
    let f = Fixture::new().await;
    let mut writer = f.open(0).await;
    enable(&mut writer);
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    age(&writer).await;
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    fault(&mut writer, Fault::Error("eviction_renamed", 5));
    assert!(maintain(&writer, &cache).await.is_err());
    let (nonce, bytes): (String, i64) =
        sqlx::query_as("SELECT nonce,bytes FROM legacy_evictions_v1")
            .fetch_one(&cache.db)
            .await
            .unwrap();
    sqlx::query("DELETE FROM RetiredEntries")
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    sqlx::query("INSERT INTO Entries(Key,Folder,Length,Bytes,Access,Pinned,ItemId,Generation,ChunkSize) VALUES($1,'a',$2,$3,0,1,$4,$5,0)")
        .bind(&entry.key).bind(SIZE.cast_signed()).bind(bytes).bind(ITEM.simple().to_string()).bind(GENERATION).execute(&writer.legacy.db).await.unwrap();
    assert!(maintain(&writer, &cache).await.is_err());
    assert_eq!(counts(&writer, &cache).await, (1, 0, 1));
    let payload = f
        .path
        .join("a/v1")
        .join(&entry.key[..2])
        .join(format!("rust-evict-{nonce}/content.data"));
    assert_eq!(std::fs::read(payload).unwrap(), super::bytes(0));
}

#[tokio::test]
async fn catalogue_administration_preserves_exact_identity_and_protected_pins() {
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    let cache = open_cache(&f).await;
    put(&writer, 0).await.unwrap();
    another(&writer, Uuid::new_v4()).await;
    let page = writer
        .legacy
        .inventory("", None, "", 1, Some(&writer))
        .await
        .unwrap();
    assert_eq!(page.entries.len(), 1);
    assert!(page.next.is_some());
    assert_eq!(page.folders.iter().map(|f| f.files).sum::<i64>(), 2);
    let second = writer
        .legacy
        .inventory(page.next.as_deref().unwrap(), None, "", 1, Some(&writer))
        .await
        .unwrap();
    assert_eq!(second.entries.len(), 1);
    assert_ne!(second.entries[0].key, page.entries[0].key);
    assert!(second.next.is_none());
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    let identity = crate::legacy::EntryIdentity::from(&entry);
    assert_eq!(
        writer.legacy.probe(&identity, 0).await.unwrap(),
        Some(BLOCK)
    );
    assert!(writer.legacy.probe(&identity, 1).await.is_err());
    assert!(
        writer
            .legacy
            .label(&identity, "fixture%_entry")
            .await
            .unwrap()
    );
    assert!(
        !writer
            .legacy
            .label(&identity, "replacement label")
            .await
            .unwrap()
    );
    assert_eq!(
        writer
            .legacy
            .inventory("", None, "%_", 50, Some(&writer))
            .await
            .unwrap()
            .entries
            .len(),
        1
    );
    assert!(
        writer
            .legacy
            .inventory("", Some("not-configured"), "", 50, Some(&writer))
            .await
            .unwrap()
            .entries
            .is_empty()
    );
    let mut stale = identity.clone();
    stale.generation = Some("changed-generation".into());
    assert!(!writer.legacy.manual_pin(&stale, true).await.unwrap());
    assert!(writer.legacy.manual_pin(&identity, true).await.unwrap());
    assert!(writer.legacy.manual_pin(&identity, false).await.unwrap());
    for pin in [2, 3] {
        sqlx::query("UPDATE Entries SET Pinned=$1 WHERE Key=$2")
            .bind(pin)
            .bind(&entry.key)
            .execute(&writer.legacy.db)
            .await
            .unwrap();
        assert!(!writer.legacy.manual_pin(&identity, false).await.unwrap());
        assert!(!writer.legacy.manual_pin(&identity, true).await.unwrap());
    }
    sqlx::query("UPDATE Entries SET Pinned=0,PendingBytes=1 WHERE Key=$1")
        .bind(&entry.key)
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    assert!(!writer.legacy.manual_pin(&identity, true).await.unwrap());
    let inventory = writer
        .legacy
        .inventory("", None, "", 50, Some(&writer))
        .await
        .unwrap();
    assert_eq!(
        inventory
            .folders
            .iter()
            .map(|f| f.pending_bytes)
            .sum::<i64>(),
        1
    );
    assert_eq!(
        inventory.folders.iter().map(|f| f.held_files).sum::<i64>(),
        1
    );
    sqlx::query("UPDATE Entries SET PendingBytes=0 WHERE Key=$1")
        .bind(&entry.key)
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    assert!(
        !writer
            .manual_evict(identity.clone(), cache.legacy_maintenance())
            .await
            .unwrap()
    );
    enable(&mut writer);
    let lease = cache
        .lease(Key::new(ITEM, b"active-administration", SIZE))
        .unwrap();
    assert!(
        !writer
            .manual_evict(identity.clone(), cache.legacy_maintenance())
            .await
            .unwrap()
    );
    drop(lease);
    assert!(
        !writer
            .manual_evict(stale, cache.legacy_maintenance())
            .await
            .unwrap()
    );
    assert!(
        writer
            .manual_evict(identity, cache.legacy_maintenance())
            .await
            .unwrap()
    );
    assert!(
        writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(counts(&writer, &cache).await, (1, 0, 0));
    assert_eq!(
        cache.evictions(None, 100).await.unwrap()[0].reason,
        "legacy:manual"
    );
}

#[tokio::test]
async fn catalogue_filters_sort_and_page_all_matches_with_stable_ties() {
    use crate::legacy::{Browse, InventoryFilter as Filter, InventorySort as Sort};
    let fixture = Fixture::new().await;
    let writer = fixture.open(8).await;
    for (n, name, access, verified, pin) in [
        (1, "alpha", 5, 25, 0),
        (2, "alpha", 5, 100, 0),
        (3, "zeta", 2, 50, 0),
        (4, "empty", 8, 0, 0),
        (5, "fixture%_literal", 1, 100, 2),
        (6, "beta", 5, 75, 0),
    ] {
        sqlx::query("INSERT INTO Entries(Key,Folder,Length,Access,ItemId,VerifiedBytes,DisplayName,Pinned) VALUES($1,'a',100,$2,$3,$4,$5,$6)")
            .bind(format!("{n:064x}")).bind(access).bind(Uuid::from_u128(n).simple().to_string()).bind(verified).bind(name).bind(pin).execute(&writer.legacy.db).await.unwrap();
    }
    // Links use canonical v2 UUIDs; original cache metadata uses compact v1 IDs.
    for search in [
        Uuid::from_u128(1).to_string(),
        Uuid::from_u128(1).simple().to_string(),
    ] {
        let page = writer
            .legacy
            .inventory_page(
                &Browse {
                    search: &search,
                    limit: 50,
                    ..Browse::default()
                },
                None,
            )
            .await
            .unwrap();
        assert_eq!(page.matched_files, 1);
        assert_eq!(
            page.entries[0].item,
            Uuid::from_u128(1).simple().to_string()
        );
    }
    // Also accept old catalogues which recorded a hyphenated identity.
    sqlx::query("UPDATE Entries SET ItemId=$1 WHERE Key=$2")
        .bind(Uuid::from_u128(6).to_string())
        .bind(format!("{:064x}", 6))
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    let page = writer
        .legacy
        .inventory_page(
            &Browse {
                search: &Uuid::from_u128(6).simple().to_string(),
                limit: 50,
                ..Browse::default()
            },
            None,
        )
        .await
        .unwrap();
    assert_eq!(page.matched_files, 1);

    for (sort, expected) in [
        (Sort::Key, vec![1, 2, 3, 4, 5, 6]),
        (Sort::Recent, vec![4, 1, 2, 6, 3, 5]),
        (Sort::Name, vec![1, 2, 6, 4, 5, 3]),
        (Sort::Coverage, vec![2, 5, 6, 3, 1, 4]),
    ] {
        let mut cursor = String::new();
        let mut keys = Vec::new();
        loop {
            let page = writer
                .legacy
                .inventory_page(
                    &Browse {
                        after: &cursor,
                        limit: 2,
                        sort,
                        ..Browse::default()
                    },
                    Some(&writer),
                )
                .await
                .unwrap();
            assert_eq!(page.matched_files, 6);
            keys.extend(page.entries.iter().map(|e| e.key.clone()));
            if let Some(next) = page.next {
                assert_ne!(cursor, next);
                cursor = next;
            } else {
                break;
            }
            assert!(keys.len() <= 6);
        }
        assert_eq!(
            keys,
            expected
                .into_iter()
                .map(|n| format!("{n:064x}"))
                .collect::<Vec<_>>()
        );
    }
    for (coverage, count) in [
        (Filter::All, 6),
        (Filter::Cached, 5),
        (Filter::Complete, 2),
        (Filter::Partial, 3),
        (Filter::Empty, 1),
        (Filter::Held, 1),
    ] {
        let page = writer
            .legacy
            .inventory_page(
                &Browse {
                    limit: 100,
                    coverage,
                    ..Browse::default()
                },
                Some(&writer),
            )
            .await
            .unwrap();
        assert_eq!(page.matched_files, count);
        assert_eq!(page.entries.len(), usize::try_from(count).unwrap());
    }
    sqlx::query("UPDATE Entries SET DisplayName='' WHERE Key=$1")
        .bind(format!("{:064x}", 1))
        .execute(&writer.legacy.db)
        .await
        .unwrap();
    let labels = std::collections::HashMap::from([(
        Uuid::from_u128(1).simple().to_string(),
        "resolved current label".to_owned(),
    )]);
    let resolved = writer
        .legacy
        .inventory_page(
            &Browse {
                limit: 50,
                search: "resolved current",
                sort: Sort::Name,
                names: Some(&labels),
                ..Browse::default()
            },
            None,
        )
        .await
        .unwrap();
    assert_eq!(resolved.matched_files, 1);
    assert_eq!(
        resolved.entries[0].name.as_deref(),
        Some("resolved current label")
    );
    let unchanged: String = sqlx::query_scalar("SELECT DisplayName FROM Entries WHERE Key=$1")
        .bind(format!("{:064x}", 1))
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert_eq!(unchanged, "");
    let literal = writer
        .legacy
        .inventory_page(
            &Browse {
                limit: 50,
                search: "%_",
                sort: Sort::Name,
                ..Browse::default()
            },
            None,
        )
        .await
        .unwrap();
    assert_eq!(literal.matched_files, 1);
    let first = writer
        .legacy
        .inventory_page(
            &Browse {
                limit: 1,
                sort: Sort::Name,
                ..Browse::default()
            },
            None,
        )
        .await
        .unwrap();
    let after = first.next.unwrap();
    assert!(
        writer
            .legacy
            .inventory_page(
                &Browse {
                    after: &after,
                    limit: 1,
                    sort: Sort::Recent,
                    ..Browse::default()
                },
                None
            )
            .await
            .is_err()
    );
    assert!(
        writer
            .legacy
            .inventory_page(
                &Browse {
                    after: &after,
                    limit: 1,
                    sort: Sort::Name,
                    search: "changed",
                    ..Browse::default()
                },
                None
            )
            .await
            .is_err()
    );
}

#[tokio::test]
async fn original_eviction_history_is_read_only_filtered_and_cursor_paged() {
    let fixture = Fixture::new().await;
    let writer = fixture.open(8).await;
    assert!(
        !writer
            .legacy
            .original_evictions(None, "", None, None, 1)
            .await
            .unwrap()
            .available
    );
    sqlx::raw_sql("CREATE TABLE CacheEvictions(Id INTEGER PRIMARY KEY,Key TEXT NOT NULL,Folder TEXT NOT NULL,ItemId TEXT NOT NULL,DisplayName TEXT,Length INTEGER,AllocatedBytes INTEGER,VerifiedBytes INTEGER,Reason TEXT,EvictedUnix INTEGER)").execute(&writer.legacy.db).await.unwrap();
    for (id, folder, name, reason) in [
        (1, "a", "fixture%_literal", "age"),
        (2, "a", "fixture older", "pressure"),
        (3, "b", "fixture newer", "clear"),
    ] {
        sqlx::query("INSERT INTO CacheEvictions VALUES($1,$2,$3,$4,$5,100,80,50,$6,1700000000)")
            .bind(id)
            .bind(format!("{id:064x}"))
            .bind(folder)
            .bind(
                Uuid::from_u128(u128::try_from(id).unwrap())
                    .simple()
                    .to_string(),
            )
            .bind(name)
            .bind(reason)
            .execute(&writer.legacy.db)
            .await
            .unwrap();
    }
    let by_id = writer
        .legacy
        .original_evictions(None, &Uuid::from_u128(1).to_string(), None, None, 50)
        .await
        .unwrap();
    assert_eq!(by_id.total, 1);
    assert_eq!(by_id.entries.len(), 1);

    let first = writer
        .legacy
        .original_evictions(None, "", None, None, 2)
        .await
        .unwrap();
    assert_eq!(first.total, 3);
    assert_eq!(first.entries[0].cursor, 3);
    assert_eq!(first.next, Some(2));
    let second = writer
        .legacy
        .original_evictions(first.next, "", None, None, 2)
        .await
        .unwrap();
    assert_eq!(second.entries.len(), 1);
    assert_eq!(second.entries[0].cursor, 1);
    assert!(second.next.is_none());
    assert_eq!(second.entries[0].at_ms, 1_700_000_000_000);
    assert_eq!(
        writer
            .legacy
            .original_evictions(None, "%_", None, None, 50)
            .await
            .unwrap()
            .total,
        1
    );
    assert_eq!(
        writer
            .legacy
            .original_evictions(None, "", Some("age"), Some("a"), 50)
            .await
            .unwrap()
            .total,
        1
    );
    assert_eq!(
        writer
            .legacy
            .original_evictions(None, "", Some("clear"), Some("a"), 50)
            .await
            .unwrap()
            .total,
        0
    );
    let count: i64 = sqlx::query_scalar("SELECT count(*) FROM CacheEvictions")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert_eq!(count, 3);
}

#[tokio::test]
async fn service_eviction_history_filters_all_records_before_paging() {
    let fixture = Fixture::new().await;
    let cache = open_cache(&fixture).await;
    for (id, revision, reason) in [
        (1, "fixture%_literal", "legacy:manual"),
        (2, "fixture other", "legacy:policy"),
        (3, "fixture newer", "manual"),
    ] {
        sqlx::query("INSERT INTO evictions(item,revision,bytes,at,reason) VALUES($1,$2,100,1700000000000,$3)").bind(Uuid::from_u128(id).to_string()).bind(revision).bind(reason).execute(&cache.db).await.unwrap();
    }
    let found = cache
        .evictions_filtered(None, 1, Some("legacy:policy"), "")
        .await
        .unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].item, Uuid::from_u128(2));
    let literal = cache.evictions_filtered(None, 1, None, "%_").await.unwrap();
    assert_eq!(literal.len(), 1);
    assert_eq!(literal[0].item, Uuid::from_u128(1));
    let newest = cache.evictions_filtered(None, 1, None, "").await.unwrap();
    let next = cache
        .evictions_filtered(Some(newest[0].cursor), 1, None, "")
        .await
        .unwrap();
    assert_eq!(newest[0].item, Uuid::from_u128(3));
    assert_eq!(next[0].item, Uuid::from_u128(2));
    assert_eq!(cache.evictions(None, 100).await.unwrap().len(), 3);
}
