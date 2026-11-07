//! Synthetic replicas of the documented 1.x layout, plus an opt-in real C# writer fixture.
#![allow(clippy::unwrap_used)]
use bytes::Bytes;
use infinidysk_native_cache::legacy::{Folder, Legacy};
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, FolderSettings, Key, Settings};
use nix::fcntl::{Flock, FlockArg};
use sha2::{Digest, Sha256};
use sqlx::sqlite::{SqliteConnectOptions, SqlitePoolOptions};
use std::fs::{File, OpenOptions};
use std::os::unix::fs::{FileExt, MetadataExt, symlink};
use std::path::PathBuf;
use uuid::Uuid;
fn hash(bytes: &[u8]) -> String {
    use std::fmt::Write;
    bytes.iter().fold(String::new(), |mut out, byte| {
        write!(out, "{byte:02x}").unwrap();
        out
    })
}
struct Fixture {
    root: PathBuf,
    catalogue: PathBuf,
    folder: Folder,
    item: Uuid,
    generation: String,
    key: Key,
    entry: String,
    bytes: Vec<u8>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}
impl Fixture {
    async fn new(chunk: i64) -> Self {
        let root = std::env::temp_dir().join(format!("id-v1-cache-{}", Uuid::new_v4()));
        let source = root.join("legacy");
        std::fs::create_dir_all(&source).unwrap();
        let item = Uuid::new_v4();
        let generation = "v3:fixture:content:revision".to_owned();
        let bytes = vec![13; 2 * BLOCK_BYTES + 17];
        let text = item.simple().to_string();
        let key_text = format!(
            "{}:{text}{}:{generation}:{}",
            text.len(),
            generation.len(),
            bytes.len()
        );
        let entry = hash(&Sha256::digest(key_text));
        let path = source.join("v1").join(&entry[..2]).join(&entry);
        std::fs::create_dir_all(&path).unwrap();
        std::fs::write(source.join(".infinidysk-owner"), b"").unwrap();
        std::fs::write(
            source.join(".infinidysk-volume"),
            "11111111111111111111111111111111",
        )
        .unwrap();
        std::fs::write(path.join("manifest.json"),serde_json::to_vec(&serde_json::json!({
            "Version":if chunk==0 {1}else{2},"Identity":{"ItemId":text,"Generation":generation,"Length":bytes.len()},"ChunkSize":chunk
        })).unwrap()).unwrap();
        let catalogue = root.join("catalogue.db");
        let db = SqlitePoolOptions::new()
            .max_connections(1)
            .connect_with(
                SqliteConnectOptions::new()
                    .filename(&catalogue)
                    .create_if_missing(true),
            )
            .await
            .unwrap();
        for sql in [
            "CREATE TABLE FolderIdentity(Folder TEXT PRIMARY KEY,Volume TEXT,RootIdentity TEXT)",
            "CREATE TABLE Entries(Key TEXT PRIMARY KEY,Folder TEXT,ItemId TEXT,Length INTEGER,Pinned INTEGER,Generation TEXT,ChunkSize INTEGER)",
            "CREATE TABLE Blocks(Key TEXT,Offset INTEGER,Count INTEGER,Hash BLOB,PRIMARY KEY(Key,Offset))",
        ] {
            sqlx::query(sql).execute(&db).await.unwrap();
        }
        let stat = std::fs::metadata(&source).unwrap();
        sqlx::query("INSERT INTO FolderIdentity VALUES('disk',$1,$2)")
            .bind("11111111111111111111111111111111")
            .bind(format!(
                "{}:{}:{}",
                nix::sys::stat::major(stat.dev()),
                nix::sys::stat::minor(stat.dev()),
                stat.ino()
            ))
            .execute(&db)
            .await
            .unwrap();
        sqlx::query("INSERT INTO Entries VALUES($1,'disk',$2,$3,1,$4,$5)")
            .bind(&entry)
            .bind(item.simple().to_string())
            .bind(i64::try_from(bytes.len()).unwrap())
            .bind(&generation)
            .bind(chunk)
            .execute(&db)
            .await
            .unwrap();
        for (index, data) in bytes.chunks(BLOCK_BYTES).enumerate() {
            let offset = index as u64 * BLOCK;
            let name = if chunk == 0 {
                "content.data".into()
            } else {
                format!("chunk-{:016x}.data", offset / chunk.cast_unsigned())
            };
            OpenOptions::new()
                .write(true)
                .create(true)
                .truncate(false)
                .open(path.join(name))
                .unwrap()
                .write_all_at(
                    data,
                    if chunk == 0 {
                        offset
                    } else {
                        offset % chunk.cast_unsigned()
                    },
                )
                .unwrap();
            sqlx::query("INSERT INTO Blocks VALUES($1,$2,$3,$4)")
                .bind(&entry)
                .bind(offset.cast_signed())
                .bind(i64::try_from(data.len()).unwrap())
                .bind(Sha256::digest(data).to_vec())
                .execute(&db)
                .await
                .unwrap();
        }
        db.close().await;
        Self {
            key: Key::new(item, b"converted source", bytes.len() as u64),
            root,
            catalogue,
            folder: Folder {
                owner_lock_path: None,
                id: "disk".into(),
                path: source.to_str().unwrap().into(),
            },
            item,
            generation,
            entry,
            bytes,
        }
    }
    fn payload(&self) -> PathBuf {
        PathBuf::from(&self.folder.path)
            .join("v1")
            .join(&self.entry[..2])
            .join(&self.entry)
            .join("content.data")
    }
    async fn destination(&self) -> std::sync::Arc<Cache> {
        let path = self.root.join("rust");
        std::fs::create_dir_all(&path).unwrap();
        Cache::open(Settings {
            enabled: true,
            metadata_path: self.root.join("rust-metadata").to_str().unwrap().into(),
            min_file_mb: 0,
            folders: vec![FolderSettings {
                id: Uuid::new_v4(),
                path: path.to_str().unwrap().into(),
                min_free_bytes: 0,
                ..FolderSettings::default()
            }],
            ..Settings::default()
        })
        .await
        .unwrap()
    }
}
#[tokio::test]
async fn sparse_and_chunked_legacy_blocks_copy_without_source_access() {
    for chunk in [0, 2 * BLOCK.cast_signed()] {
        let fixture = Fixture::new(chunk).await;
        let before = std::fs::read(&fixture.catalogue).unwrap();
        let legacy = Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
            .await
            .unwrap();
        let entry = legacy.entries("", 1).await.unwrap().pop().unwrap();
        assert!(Legacy::matches(
            &entry,
            fixture.item,
            &fixture.generation,
            fixture.key.size()
        ));
        assert!(!Legacy::matches(
            &entry,
            fixture.item,
            "stale",
            fixture.key.size()
        ));
        assert!(!Legacy::matches(
            &entry,
            fixture.item,
            &fixture.generation,
            fixture.key.size() + 1
        ));
        let cache = fixture.destination().await;
        let blocks = legacy.blocks(&entry, -1, 64).await.unwrap();
        for block in blocks {
            assert!(
                legacy
                    .copy_block(&cache, &entry, &fixture.key, block)
                    .await
                    .unwrap()
            );
        }
        let lease = cache.lease(fixture.key.clone()).unwrap();
        for (index, expected) in fixture.bytes.chunks(BLOCK_BYTES).enumerate() {
            assert_eq!(
                cache.get(&lease, index as u64).await.unwrap(),
                Bytes::copy_from_slice(expected)
            );
        }
        // Repeating the same cursor safely reuses complete verified blocks.
        assert!(
            legacy
                .copy_block(
                    &cache,
                    &entry,
                    &fixture.key,
                    legacy.blocks(&entry, -1, 1).await.unwrap().pop().unwrap()
                )
                .await
                .unwrap()
        );
        assert_eq!(
            cache.verify_complete(&fixture.key).await.unwrap(),
            Some(true)
        );
        assert_eq!(std::fs::read(&fixture.catalogue).unwrap(), before);
    }
}
#[tokio::test]
async fn corrupted_or_linked_legacy_payload_is_never_published() {
    let fixture = Fixture::new(0).await;
    let legacy = Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
        .await
        .unwrap();
    let entry = legacy.entries("", 1).await.unwrap().pop().unwrap();
    let cache = fixture.destination().await;
    OpenOptions::new()
        .write(true)
        .open(fixture.payload())
        .unwrap()
        .write_all_at(b"bad", 0)
        .unwrap();
    let block = legacy.blocks(&entry, -1, 1).await.unwrap().pop().unwrap();
    assert!(
        legacy
            .copy_block(&cache, &entry, &fixture.key, block)
            .await
            .is_err()
    );
    assert!(cache.coverage(&fixture.key).await.unwrap().is_empty());
    std::fs::remove_file(fixture.payload()).unwrap();
    symlink("/dev/zero", fixture.payload()).unwrap();
    let block = legacy.blocks(&entry, -1, 1).await.unwrap().pop().unwrap();
    assert!(
        legacy
            .copy_block(&cache, &entry, &fixture.key, block)
            .await
            .is_err()
    );
    assert!(cache.coverage(&fixture.key).await.unwrap().is_empty());
}
#[tokio::test]
async fn live_owner_or_missing_volume_refuses_legacy_adoption() {
    let fixture = Fixture::new(0).await;
    let owner = Flock::lock(
        File::open(PathBuf::from(&fixture.folder.path).join(".infinidysk-owner")).unwrap(),
        FlockArg::LockExclusiveNonblock,
    )
    .unwrap();
    assert!(
        Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
            .await
            .is_err()
    );
    drop(owner);
    std::fs::remove_file(PathBuf::from(&fixture.folder.path).join(".infinidysk-volume")).unwrap();
    assert!(
        Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
            .await
            .is_err()
    );
}

#[tokio::test]
async fn direct_reads_preserve_sparse_and_chunked_payloads_and_reject_corruption() {
    for chunk in [0, 2 * BLOCK.cast_signed()] {
        let fixture = Fixture::new(chunk).await;
        let legacy = Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
            .await
            .unwrap();
        let entry = legacy
            .find(
                fixture.item,
                &fixture.generation,
                fixture.bytes.len() as u64,
            )
            .await
            .unwrap()
            .unwrap();
        assert!(
            legacy
                .find(fixture.item, "changed", fixture.bytes.len() as u64)
                .await
                .unwrap()
                .is_none()
        );
        let slots = std::sync::Arc::new(tokio::sync::Semaphore::new(1));
        for block in 0..3 {
            let bytes = legacy
                .read_block(&entry, block, slots.clone().acquire_owned().await.unwrap())
                .await
                .unwrap()
                .unwrap();
            let start = usize::try_from(block).unwrap() * BLOCK_BYTES;
            assert_eq!(
                bytes.as_ref(),
                &fixture.bytes[start..(start + BLOCK_BYTES).min(fixture.bytes.len())]
            );
        }
        assert!(
            legacy
                .read_block(&entry, 3, slots.clone().acquire_owned().await.unwrap())
                .await
                .unwrap()
                .is_none()
        );
        let payload = if chunk == 0 {
            fixture.payload()
        } else {
            PathBuf::from(&fixture.folder.path)
                .join("v1")
                .join(&fixture.entry[..2])
                .join(&fixture.entry)
                .join("chunk-0000000000000000.data")
        };
        let before = std::fs::read(&payload).unwrap();
        let mut bad = before.clone();
        bad[0] ^= 1;
        std::fs::write(&payload, &bad).unwrap();
        assert!(
            legacy
                .read_block(&entry, 0, slots.acquire_owned().await.unwrap())
                .await
                .is_err()
        );
        assert_eq!(
            std::fs::read(&payload).unwrap(),
            bad,
            "reader must not repair or rewrite corrupt data"
        );
    }
}

#[tokio::test]
async fn legacy_settings_reject_aliases_before_creating_destination_files() {
    let fixture = Fixture::new(0).await;
    let alias = fixture.root.join("alias");
    symlink(&fixture.folder.path, &alias).unwrap();
    std::fs::create_dir(fixture.root.join("source-meta")).unwrap();
    let settings = Settings {
        enabled: true,
        metadata_path: alias.join("must-not-create").to_string_lossy().into_owned(),
        legacy: Some(infinidysk_native_cache::legacy::Settings {
            write: None,
            metadata_path: fixture
                .root
                .join("source-meta")
                .to_string_lossy()
                .into_owned(),
            folders: vec![fixture.folder.clone()],
        }),
        ..Settings::default()
    };
    assert!(Cache::open(settings).await.is_err());
    assert!(!alias.join("must-not-create").exists());
}

#[test]
fn cancelled_direct_read_keeps_physical_ownership_until_queued_io_finishes() {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(1)
        .max_blocking_threads(1)
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let fixture = Fixture::new(0).await;
        let legacy = Legacy::open(&fixture.catalogue, std::slice::from_ref(&fixture.folder))
            .await
            .unwrap();
        let entry = legacy
            .find(
                fixture.item,
                &fixture.generation,
                fixture.bytes.len() as u64,
            )
            .await
            .unwrap()
            .unwrap();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let (entered_tx, entered_rx) = tokio::sync::oneshot::channel();
        let blocker = tokio::task::spawn_blocking(move || {
            entered_tx.send(()).unwrap();
            release_rx.recv().unwrap();
        });
        entered_rx.await.unwrap();
        let slots = std::sync::Arc::new(tokio::sync::Semaphore::new(1));
        let result = tokio::time::timeout(
            std::time::Duration::from_millis(100),
            legacy.read_block(&entry, 0, slots.clone().acquire_owned().await.unwrap()),
        )
        .await;
        assert!(result.is_err());
        drop(legacy);
        assert_eq!(slots.available_permits(), 0);
        let owner = PathBuf::from(&fixture.folder.path).join(".infinidysk-owner");
        assert!(Flock::lock(File::open(&owner).unwrap(), FlockArg::LockExclusiveNonblock).is_err());
        release_tx.send(()).unwrap();
        blocker.await.unwrap();
        // The owned read also finishes metadata work on Tokio after physical I/O.
        // A blocking-pool barrier alone does not prove that lifecycle has ended.
        let finished = tokio::time::timeout(std::time::Duration::from_secs(10), async {
            let permit = slots.clone().acquire_owned().await.unwrap();
            drop(permit);
            loop {
                if let Ok(owner) =
                    Flock::lock(File::open(&owner).unwrap(), FlockArg::LockExclusiveNonblock)
                {
                    return owner;
                }
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        drop(finished);
        assert_eq!(slots.available_permits(), 1);
        assert!(Flock::lock(File::open(owner).unwrap(), FlockArg::LockExclusiveNonblock).is_ok());
    });
}

#[tokio::test]
#[ignore = "requires the real 1.x C# writer fixture; see scripts/fixtures/legacy-native-cache"]
async fn real_csharp_writer_payloads_are_reused_and_partial_coverage_stays_partial() {
    let path = std::env::var("INFINIDYSK_LEGACY_CACHE_FIXTURE").expect("fixture receipt path");
    let receipts: Vec<serde_json::Value> =
        serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
    for receipt in receipts {
        let dir = PathBuf::from(receipt["metadata_path"].as_str().unwrap());
        let folder = Folder {
            owner_lock_path: None,
            id: receipt["folder_id"].as_str().unwrap().into(),
            path: receipt["folder_path"].as_str().unwrap().into(),
        };
        let legacy = Legacy::open(&dir.join("catalogue.db"), &[folder])
            .await
            .unwrap();
        let entry = legacy.entries("", 1).await.unwrap().pop().unwrap();
        let item = Uuid::parse_str(receipt["item"].as_str().unwrap()).unwrap();
        let generation = receipt["generation"].as_str().unwrap();
        let size = receipt["length"].as_u64().unwrap();
        assert!(Legacy::matches(&entry, item, generation, size));
        let t = Fixture::new(0).await;
        let cache = t.destination().await;
        let key = Key::new(item, b"converted revision", size);
        for block in legacy.blocks(&entry, -1, 64).await.unwrap() {
            assert!(
                legacy
                    .copy_block(&cache, &entry, &key, block)
                    .await
                    .unwrap()
            );
        }
        let partial = receipt["mode"] == "partial";
        assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(!partial));
        let lease = cache.lease(key).unwrap();
        let mut digest = Sha256::new();
        for block in 0..size.div_ceil(BLOCK) {
            if partial && block == 1 {
                assert!(cache.get(&lease, block).await.is_none());
                continue;
            }
            let data = cache.get(&lease, block).await.unwrap();
            let offset = block * BLOCK;
            assert!(
                data.iter()
                    .enumerate()
                    .all(|(index, byte)| *byte == ((offset + index as u64) % 251) as u8)
            );
            digest.update(&data);
        }
        if !partial {
            assert_eq!(
                hash(&digest.finalize()),
                receipt["sha256"].as_str().unwrap()
            );
        }
    }
}
