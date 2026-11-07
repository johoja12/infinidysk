#![allow(clippy::unwrap_used)] // Private generated fixtures; failures are assertions.
use super::*;
use std::os::unix::fs::{MetadataExt, symlink};
use std::path::{Path, PathBuf};

struct Fixture {
    path: PathBuf,
    folders: Vec<Folder>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}
impl Fixture {
    async fn new() -> Self {
        let path = std::env::temp_dir().join(format!("id-compatible-cache-{}", Uuid::new_v4()));
        std::fs::create_dir_all(path.join("meta")).unwrap();
        std::fs::write(
            path.join("fixture-owner"),
            b"generated-native-cache-write-tests",
        )
        .unwrap();
        let db = SqlitePool::connect_with(
            sqlx::sqlite::SqliteConnectOptions::new()
                .filename(path.join("meta/catalogue.db"))
                .create_if_missing(true),
        )
        .await
        .unwrap();
        sqlx::raw_sql(include_str!(
            "../../../../../scripts/fixtures/legacy-native-cache/catalogue-schema.sql"
        ))
        .execute(&db)
        .await
        .unwrap();
        let mut folders = Vec::new();
        for id in ["a", "b"] {
            let root = path.join(id);
            std::fs::create_dir(&root).unwrap();
            let volume = Uuid::new_v4().simple().to_string();
            std::fs::write(root.join(".infinidysk-volume"), &volume).unwrap();
            std::fs::write(root.join(".infinidysk-owner"), b"").unwrap();
            let stat = root.metadata().unwrap();
            sqlx::query("INSERT INTO FolderIdentity VALUES($1,$2,$3)")
                .bind(id)
                .bind(volume)
                .bind(format!(
                    "{}:{}:{}",
                    nix::sys::stat::major(stat.dev()),
                    nix::sys::stat::minor(stat.dev()),
                    stat.ino()
                ))
                .execute(&db)
                .await
                .unwrap();
            folders.push(Folder {
                owner_lock_path: None,
                id: id.into(),
                path: root.to_string_lossy().into_owned(),
            });
        }
        db.close().await;
        Self { path, folders }
    }
    fn settings(&self, chunk_mb: u32) -> WriteSettings {
        WriteSettings {
            chunk_mb,
            folders: self
                .folders
                .iter()
                .enumerate()
                .map(|(i, f)| WriteFolder {
                    id: f.id.clone(),
                    priority: -i32::try_from(i).unwrap(),
                    quota_bytes: 100 * BLOCK,
                    min_free_bytes: 0,
                })
                .collect(),
        }
    }
    async fn open(&self, chunk_mb: u32) -> Arc<Writer> {
        let legacy = Arc::new(
            Legacy::open_for_writes(&self.path.join("meta/catalogue.db"), &self.folders)
                .await
                .unwrap(),
        );
        Writer::new(legacy, self.settings(chunk_mb)).unwrap()
    }
}
#[tokio::test]
#[ignore = "explicitly exports generated fixtures for original C# readback qualification"]
async fn export_original_csharp_readback_fixtures() {
    let output = PathBuf::from(std::env::var("ID_COMPATIBLE_CACHE_EXPORT").unwrap());
    assert!(output.is_absolute());
    let mut rows = Vec::new();
    for chunk in [0, 8] {
        let f = std::mem::ManuallyDrop::new(Fixture::new().await);
        let writer = f.open(chunk).await;
        let mut digest = Sha256::new();
        for block in 0..4 {
            assert!(put(&writer, block).await.unwrap());
            digest.update(bytes(block));
        }
        let entry = writer
            .legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap();
        rows.push(serde_json::json!({"fixture_root":f.path,"metadata_path":f.path.join("meta"),"folder_id":entry.folder,"folder_path":f.path.join(&entry.folder),"item":ITEM.simple().to_string(),"generation":GENERATION,"length":SIZE,"sha256":hex(&digest.finalize())}));
        writer.legacy.db.close().await;
        drop(writer);
    }
    let file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(output)
        .unwrap();
    serde_json::to_writer(file, &rows).unwrap();
}

const ITEM: Uuid = Uuid::from_u128(42);
const GENERATION: &str = "rust-v2:fixture-content-revision";
const SIZE: u64 = 3 * BLOCK + 37;
fn bytes(block: u64) -> Bytes {
    Bytes::from(vec![
        u8::try_from(block + 1).unwrap();
        usize::try_from(BLOCK.min(SIZE - block * BLOCK))
            .unwrap()
    ])
}
async fn put(writer: &Arc<Writer>, block: u64) -> Result<bool, Error> {
    writer
        .put(
            ITEM,
            GENERATION.into(),
            SIZE,
            block,
            bytes(block),
            Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap(),
        )
        .await
}
async fn read(legacy: &Legacy, block: u64) -> Option<Bytes> {
    let entry = legacy.find(ITEM, GENERATION, SIZE).await.unwrap()?;
    legacy
        .read_block(
            &entry,
            block,
            Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap(),
        )
        .await
        .unwrap()
}
async fn accounting(legacy: &Legacy) -> (i64, i64, i64) {
    sqlx::query_as("SELECT Bytes,PendingBytes,VerifiedBytes FROM Entries LIMIT 1")
        .fetch_one(&legacy.db)
        .await
        .unwrap()
}
fn fault(writer: &mut Arc<Writer>, fault: Fault) {
    Arc::get_mut(writer).unwrap().fault = Some(fault);
}

#[tokio::test]
async fn sparse_and_chunked_publication_is_concurrent_restart_safe_and_never_overwrites_committed_bytes()
 {
    for chunk in [0, 8] {
        let f = Fixture::new().await;
        let writer = f.open(chunk).await;
        let (first, second, third, last, duplicate) = tokio::join!(
            put(&writer, 0),
            put(&writer, 1),
            put(&writer, 2),
            put(&writer, 3),
            put(&writer, 0)
        );
        assert!(
            first.unwrap()
                && second.unwrap()
                && third.unwrap()
                && last.unwrap()
                && duplicate.unwrap()
        );
        assert_eq!(writer.written_bytes(), SIZE);
        for block in 0..4 {
            assert_eq!(read(&writer.legacy, block).await.unwrap(), bytes(block));
        }
        let before = accounting(&writer.legacy).await;
        assert_eq!(before.1, 0);
        assert_eq!(before.2, SIZE.cast_signed());
        assert!(put(&writer, 0).await.unwrap());
        assert_eq!(accounting(&writer.legacy).await, before);
        assert!(
            writer
                .put(
                    ITEM,
                    GENERATION.into(),
                    SIZE,
                    0,
                    Bytes::from(vec![99; usize::try_from(BLOCK).unwrap()]),
                    Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap()
                )
                .await
                .is_err()
        );
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
        writer.legacy.db.close().await;
        drop(writer);
        let legacy = Legacy::open_in_place(&f.path.join("meta/catalogue.db"), &f.folders)
            .await
            .unwrap();
        for block in 0..4 {
            assert_eq!(read(&legacy, block).await.unwrap(), bytes(block));
        }
    }
}

#[tokio::test]
async fn explicit_owner_binding_requires_the_original_inode_and_detects_replacement() {
    use nix::fcntl::{FcntlArg, fcntl};
    let f = Fixture::new().await;
    let mut folders = f.folders.clone();
    folders[0].owner_lock_path = Some(
        f.path
            .join("a/.infinidysk-owner")
            .to_string_lossy()
            .into_owned(),
    );
    let legacy = Legacy::open_in_place(&f.path.join("meta/catalogue.db"), &folders)
        .await
        .unwrap();
    let root = legacy.roots.get("a").unwrap();
    assert_eq!(
        fcntl(&*root.owner, FcntlArg::F_GETFL).unwrap() & nix::libc::O_ACCMODE,
        nix::libc::O_RDWR
    );
    std::fs::rename(
        f.path.join("a/.infinidysk-owner"),
        f.path.join("a/old-owner"),
    )
    .unwrap();
    std::fs::write(f.path.join("a/.infinidysk-owner"), b"").unwrap();
    assert!(root.validate().is_err());
    drop(legacy);
    let unrelated = f.path.join("unrelated-owner");
    std::fs::write(&unrelated, b"unchanged").unwrap();
    folders[0].owner_lock_path = Some(unrelated.to_string_lossy().into_owned());
    assert!(
        Legacy::open_in_place(&f.path.join("meta/catalogue.db"), &folders)
            .await
            .is_err()
    );
    assert_eq!(std::fs::read(unrelated).unwrap(), b"unchanged");
}

#[tokio::test]
async fn quota_and_free_space_declines_do_not_copy_or_relocate_existing_entries() {
    let f = Fixture::new().await;
    let legacy = Arc::new(
        Legacy::open_for_writes(&f.path.join("meta/catalogue.db"), &f.folders)
            .await
            .unwrap(),
    );
    let mut settings = f.settings(8);
    settings.folders[0].quota_bytes = BLOCK;
    let writer = Writer::new(legacy.clone(), settings).unwrap();
    assert!(put(&writer, 0).await.unwrap());
    assert_eq!(
        legacy
            .find(ITEM, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap()
            .folder,
        "b"
    );
    assert!(!f.path.join("a/v1").exists());
    drop(writer);
    let mut settings = f.settings(8);
    settings.folders[1].quota_bytes = BLOCK;
    let writer = Writer::new(legacy.clone(), settings).unwrap();
    assert!(!put(&writer, 1).await.unwrap());
    assert_eq!(read(&legacy, 0).await.unwrap(), bytes(0));
    assert!(read(&legacy, 1).await.is_none());
    let before = accounting(&legacy).await;
    drop(writer);
    let mut settings = f.settings(8);
    for folder in &mut settings.folders {
        folder.min_free_bytes = u64::MAX;
    }
    let writer = Writer::new(legacy.clone(), settings).unwrap();
    assert!(!put(&writer, 1).await.unwrap());
    assert_eq!(accounting(&legacy).await, before);
}

#[tokio::test]
async fn journal_readback_recovers_only_durable_matching_data_and_never_double_reserves() {
    for stage in [
        "reserved",
        "before_directory_sync",
        "before_write",
        "written",
        "before_payload_sync",
        "payload_synced",
        "journal_written",
        "before_journal_sync",
        "before_entry_sync",
        "journal_synced",
        "before_catalogue",
        "catalogue_committed",
    ] {
        let f = Fixture::new().await;
        let mut writer = f.open(8).await;
        assert!(put(&writer, 0).await.unwrap());
        fault(&mut writer, Fault::Error(stage, 5));
        assert!(put(&writer, 1).await.is_err(), "{stage}");
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
        let before = accounting(&writer.legacy).await;
        assert_eq!(
            read(&writer.legacy, 1).await.is_some(),
            stage == "catalogue_committed"
        );
        writer.legacy.db.close().await;
        drop(writer);
        let writer = f.open(8).await;
        let recovered = put(&writer, 1).await.unwrap_or(false);
        let should_recover = matches!(
            stage,
            "journal_written"
                | "before_journal_sync"
                | "before_entry_sync"
                | "journal_synced"
                | "before_catalogue"
                | "catalogue_committed"
        );
        assert_eq!(recovered, should_recover, "{stage}");
        let after = accounting(&writer.legacy).await;
        assert_eq!(
            after.0, before.0,
            "retries must not reserve more bytes: {stage}"
        );
        assert_eq!(after.1 == 0, should_recover);
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
        if should_recover {
            assert_eq!(read(&writer.legacy, 1).await.unwrap(), bytes(1));
        } else {
            assert!(read(&writer.legacy, 1).await.is_none());
        }
    }
}

#[tokio::test]
async fn short_writes_and_disk_full_keep_coverage_absent_and_preserve_other_blocks() {
    for injected in [
        Fault::Short("payload"),
        Fault::Short("journal"),
        Fault::Error("before_write", 28),
    ] {
        let f = Fixture::new().await;
        let mut writer = f.open(8).await;
        assert!(put(&writer, 0).await.unwrap());
        fault(&mut writer, injected);
        assert!(put(&writer, 1).await.is_err());
        assert!(read(&writer.legacy, 1).await.is_none());
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
        let before = accounting(&writer.legacy).await;
        writer.legacy.db.close().await;
        drop(writer);
        let writer = f.open(8).await;
        assert!(!put(&writer, 1).await.unwrap_or(false));
        assert_eq!(accounting(&writer.legacy).await, before);
    }
}

#[tokio::test]
async fn symlink_payload_and_changed_volume_never_redirect_writes() {
    let f = Fixture::new().await;
    let writer = f.open(0).await;
    assert!(put(&writer, 0).await.unwrap());
    let entry = writer
        .legacy
        .find(ITEM, GENERATION, SIZE)
        .await
        .unwrap()
        .unwrap();
    let root = writer.legacy.roots.get(&entry.folder).unwrap();
    let payload = Path::new(&root.path)
        .join("v1")
        .join(&entry.key[..2])
        .join(&entry.key)
        .join("content.data");
    let outside = f.path.join("unrelated");
    std::fs::write(&outside, b"untouched").unwrap();
    std::fs::remove_file(payload.clone()).unwrap();
    symlink(&outside, &payload).unwrap();
    assert!(put(&writer, 1).await.is_err());
    assert_eq!(std::fs::read(&outside).unwrap(), b"untouched");
    assert!(read(&writer.legacy, 1).await.is_none());
    std::fs::write(Path::new(&root.path).join(".infinidysk-volume"), b"changed").unwrap();
    let entry_count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Entries")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    let new_item = Uuid::new_v4();
    let result = writer
        .put(
            new_item,
            GENERATION.into(),
            SIZE,
            0,
            bytes(0),
            Arc::new(Semaphore::new(1)).acquire_owned().await.unwrap(),
        )
        .await;
    // Another valid folder may accept the new entry; the replaced folder must stay untouched.
    assert!(result.unwrap());
    assert_eq!(
        writer
            .legacy
            .find(new_item, GENERATION, SIZE)
            .await
            .unwrap()
            .unwrap()
            .folder,
        "b"
    );
    let after: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Entries")
        .fetch_one(&writer.legacy.db)
        .await
        .unwrap();
    assert_eq!(after, entry_count + 1);
    assert_eq!(std::fs::read(&outside).unwrap(), b"untouched");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancelled_request_retains_buffer_and_ownership_until_publication_finishes() {
    use nix::fcntl::{Flock, FlockArg};
    let f = Fixture::new().await;
    let mut writer = f.open(8).await;
    let entered = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let gate = Arc::new((Mutex::new(false), std::sync::Condvar::new()));
    fault(
        &mut writer,
        Fault::Gate("before_write", entered.clone(), gate.clone()),
    );
    let slots = Arc::new(Semaphore::new(1));
    let permit = slots.clone().acquire_owned().await.unwrap();
    let request = tokio::spawn({
        let writer = writer.clone();
        async move {
            writer
                .put(ITEM, GENERATION.into(), SIZE, 0, bytes(0), permit)
                .await
        }
    });
    for _ in 0..200 {
        if entered.load(Ordering::Acquire) {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
    }
    assert!(entered.load(Ordering::Acquire));
    request.abort();
    let _ = request.await;
    drop(writer);
    let owner = f.path.join("a/.infinidysk-owner");
    assert_eq!(slots.available_permits(), 0);
    assert!(Flock::lock(File::open(&owner).unwrap(), FlockArg::LockExclusiveNonblock).is_err());
    {
        let (lock, wake) = &*gate;
        *lock.lock().unwrap() = true;
        wake.notify_all();
    }
    for _ in 0..200 {
        if slots.available_permits() == 1 {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
    }
    assert_eq!(slots.available_permits(), 1);
    let writer = f.open(8).await;
    assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
}

#[test]
#[ignore = "subprocess helper exercised by crash-boundary qualification"]
fn crash_child() {
    let path = PathBuf::from(std::env::var("ID_COMPATIBLE_CACHE_CHILD_ROOT").unwrap());
    assert_eq!(
        std::fs::read(path.join("fixture-owner")).unwrap(),
        b"generated-native-cache-write-tests"
    );
    let stage = std::env::var("ID_COMPATIBLE_CACHE_CHILD_STAGE").unwrap();
    let stage = match stage.as_str() {
        "reserved" => "reserved",
        "before_directory_sync" => "before_directory_sync",
        "before_entry_sync" => "before_entry_sync",
        "written" => "written",
        "journal_synced" => "journal_synced",
        "before_catalogue" => "before_catalogue",
        "catalogue_committed" => "catalogue_committed",
        _ => panic!("unknown crash boundary"),
    };
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(async {
            let folders = ["a", "b"]
                .into_iter()
                .map(|id| Folder {
                    owner_lock_path: None,
                    id: id.into(),
                    path: path.join(id).to_string_lossy().into_owned(),
                })
                .collect();
            // The parent owns cleanup; crashing must not run the fixture destructor.
            let f = std::mem::ManuallyDrop::new(Fixture { path, folders });
            let mut writer = f.open(8).await;
            fault(&mut writer, Fault::Crash(stage));
            let _ = put(&writer, 1).await;
            panic!("the requested crash boundary was not reached");
        });
}

#[tokio::test]
async fn process_death_at_publication_boundaries_preserves_old_data_and_recovers_or_holds_new_data()
{
    use std::os::unix::process::ExitStatusExt;
    for stage in [
        "reserved",
        "before_directory_sync",
        "written",
        "before_entry_sync",
        "journal_synced",
        "before_catalogue",
        "catalogue_committed",
    ] {
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
            .env("ID_COMPATIBLE_CACHE_CHILD_STAGE", stage)
            .output()
            .unwrap();
        assert_eq!(
            output.status.signal(),
            Some(6),
            "{stage}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let writer = f.open(8).await;
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
        assert_eq!(
            put(&writer, 1).await.unwrap_or(false),
            matches!(
                stage,
                "before_entry_sync" | "journal_synced" | "before_catalogue" | "catalogue_committed"
            )
        );
        assert_eq!(read(&writer.legacy, 0).await.unwrap(), bytes(0));
    }
}
