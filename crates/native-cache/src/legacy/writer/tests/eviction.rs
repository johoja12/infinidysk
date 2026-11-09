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
