//! No-copy compatibility through the actual application bridge, VFS and warming queue.
#![allow(clippy::unwrap_used)] // Generated fixtures; every failure is an assertion.
use super::LegacyReader;
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, HealthStatus, Item};
use infinidysk_import::native::{Ledger, Source};
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, Key, Settings, legacy};
use infinidysk_nntp::Client;
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use sha2::{Digest, Sha256};
use sqlx::sqlite::SqliteConnectOptions;
use std::os::unix::fs::{FileExt, MetadataExt};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use uuid::Uuid;

struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn inventory(path: &Path) -> Vec<(PathBuf, Vec<u8>, u64)> {
    let mut files = Vec::new();
    for entry in std::fs::read_dir(path).unwrap() {
        let entry = entry.unwrap();
        if entry.file_type().unwrap().is_dir() {
            files.extend(inventory(&entry.path()));
        } else {
            files.push((
                entry.path(),
                Sha256::digest(std::fs::read(entry.path()).unwrap()).to_vec(),
                entry.metadata().unwrap().blocks(),
            ));
        }
    }
    files.sort_by(|a, b| a.0.cmp(&b.0));
    files
}
struct Fixture {
    temp: Temp,
    db: Db,
    settings: Settings,
    items: Vec<Item>,
    bytes: Vec<u8>,
}
impl Fixture {
    async fn new(provenance: bool) -> Self {
        let path = std::env::temp_dir().join(format!("id-in-place-{}", Uuid::new_v4()));
        std::fs::create_dir_all(path.join("legacy-meta")).unwrap();
        let temp = Temp(path);
        let db = Db::connect(&format!("sqlite:{}", temp.0.join("main.sqlite").display()))
            .await
            .unwrap();
        let options = |name: &str| {
            SqliteConnectOptions::new()
                .filename(temp.0.join("legacy-meta").join(name))
                .create_if_missing(true)
        };
        let catalogue = sqlx::SqlitePool::connect_with(options("catalogue.db"))
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE FolderIdentity(Folder TEXT PRIMARY KEY,Volume TEXT,RootIdentity TEXT); CREATE TABLE Entries(Key TEXT PRIMARY KEY,Folder TEXT,ItemId TEXT,Length INTEGER,Pinned INTEGER,Generation TEXT,ChunkSize INTEGER); CREATE TABLE Blocks(Key TEXT,Offset INTEGER,Count INTEGER,Hash BLOB,PRIMARY KEY(Key,Offset));")
            .execute(&catalogue).await.unwrap();
        let revisions = sqlx::SqlitePool::connect_with(options("repair-revisions.db"))
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE Identity(Id TEXT); INSERT INTO Identity VALUES('fixture-epoch'); CREATE TABLE Revisions(Hash TEXT,Stamp TEXT);")
            .execute(&revisions).await.unwrap();
        revisions.close().await;
        let ledger = Ledger::open(&temp.0.join("legacy-meta/repair-revisions.db"))
            .await
            .unwrap();
        let bytes: Vec<u8> = (0..2 * BLOCK_BYTES + 37)
            .map(|i| u8::try_from(i % 251).unwrap())
            .collect();
        let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
        let mut folders = Vec::new();
        let mut items = Vec::new();
        for index in 0..2 {
            let root = temp.0.join(format!("legacy-{index}"));
            std::fs::create_dir(&root).unwrap();
            let volume = Uuid::new_v4().simple().to_string();
            std::fs::write(root.join(".infinidysk-owner"), b"").unwrap();
            std::fs::write(root.join(".infinidysk-volume"), &volume).unwrap();
            let folder = format!("folder-{index}");
            let stat = root.metadata().unwrap();
            sqlx::query("INSERT INTO FolderIdentity VALUES($1,$2,$3)")
                .bind(&folder)
                .bind(volume)
                .bind(format!(
                    "{}:{}:{}",
                    nix::sys::stat::major(stat.dev()),
                    nix::sys::stat::minor(stat.dev()),
                    stat.ino()
                ))
                .execute(&catalogue)
                .await
                .unwrap();
            folders.push(legacy::Folder {
                id: folder.clone(),
                path: root.to_string_lossy().into_owned(),
            });
            let mut item =
                Item::child(&parent, &format!("fixture-{index}.dat"), ItemKind::NzbFile).unwrap();
            let meta = FileMeta::Nzb(NzbFile {
                segments: Segments {
                    items: vec![Segment {
                        id: "fixture@unavailable".into(),
                        size: u32::try_from(bytes.len()).unwrap(),
                        fallbacks: vec![],
                    }],
                    sizes_trusted: true,
                    ..Segments::default()
                },
                proof: None,
            });
            let source = Source {
                version: 1,
                base_generation: format!("v3:fixture-{index}"),
                dependencies: vec![],
                fingerprint: Key::fingerprint(&meta.content_identity().unwrap()).to_vec(),
                size: bytes.len() as u64,
                mapped_identity: None,
            };
            let generation = source.generation(&ledger).await.unwrap();
            let id = item.id.0.simple().to_string();
            let entry: String = Sha256::digest(format!(
                "{}:{id}{}:{generation}:{}",
                id.len(),
                generation.len(),
                bytes.len()
            ))
            .iter()
            .fold(String::new(), |mut out, byte| {
                use std::fmt::Write as _;
                write!(out, "{byte:02x}").unwrap();
                out
            });
            let directory = root.join("v1").join(&entry[..2]).join(&entry);
            std::fs::create_dir_all(&directory).unwrap();
            let chunk = if index == 0 { 0 } else { 2 * BLOCK };
            std::fs::write(directory.join("manifest.json"), serde_json::to_vec(&serde_json::json!({"Version":if chunk==0 {1}else{2},"Identity":{"ItemId":id,"Generation":generation,"Length":bytes.len()},"ChunkSize":chunk})).unwrap()).unwrap();
            sqlx::query("INSERT INTO Entries VALUES($1,$2,$3,$4,1,$5,$6)")
                .bind(&entry)
                .bind(folder)
                .bind(id)
                .bind(i64::try_from(bytes.len()).unwrap())
                .bind(generation)
                .bind(i64::try_from(chunk).unwrap())
                .execute(&catalogue)
                .await
                .unwrap();
            for (block, data) in bytes.chunks(BLOCK_BYTES).enumerate() {
                let start = block as u64 * BLOCK;
                let file = start.checked_div(chunk).map_or_else(
                    || "content.data".to_owned(),
                    |part| format!("chunk-{part:016x}.data"),
                );
                std::fs::OpenOptions::new()
                    .write(true)
                    .create(true)
                    .truncate(false)
                    .open(directory.join(file))
                    .unwrap()
                    .write_all_at(data, if chunk == 0 { start } else { start % chunk })
                    .unwrap();
                sqlx::query("INSERT INTO Blocks VALUES($1,$2,$3,$4)")
                    .bind(&entry)
                    .bind(i64::try_from(start).unwrap())
                    .bind(i64::try_from(data.len()).unwrap())
                    .bind(Sha256::digest(data).to_vec())
                    .execute(&catalogue)
                    .await
                    .unwrap();
            }
            let blob_id = BlobId::new();
            item.meta_blob = Some(blob_id);
            item.size = Some(bytes.len() as u64);
            let raw = blob::encode_meta(&meta).unwrap();
            let sources = if provenance {
                vec![(item.id, serde_json::to_string(&source).unwrap())]
            } else {
                vec![]
            };
            db.import_items_with_sources(
                &[(item.clone(), HealthStatus::default())],
                &[(blob_id, &raw)],
                &sources,
            )
            .await
            .unwrap();
            items.push(item);
        }
        catalogue.close().await;
        let settings = Settings {
            enabled: true,
            io_timeout_ms: 10_000,
            metadata_path: temp.0.join("rust-meta").to_string_lossy().into_owned(),
            legacy: Some(legacy::Settings {
                metadata_path: temp.0.join("legacy-meta").to_string_lossy().into_owned(),
                folders,
            }),
            ..Settings::default()
        };
        Self {
            temp,
            db,
            settings,
            items,
            bytes,
        }
    }
    async fn cache(&self) -> Arc<Cache> {
        let cache = Cache::open(self.settings.clone()).await.unwrap();
        cache
            .set_legacy_reader(
                LegacyReader::open(self.db.clone(), self.settings.legacy.as_ref().unwrap())
                    .await
                    .unwrap(),
            )
            .unwrap();
        cache
    }
    fn vfs(&self, cache: Arc<Cache>) -> Arc<Vfs> {
        Arc::new(
            Vfs::new(
                self.db.clone(),
                Arc::new(Client::new([])),
                infinidysk_stream::Budget::new(8 * 1024 * 1024),
                1024 * 1024,
                "/unused",
            )
            .with_native_cache(Some(cache)),
        )
    }
    fn legacy_inventory(&self) -> Vec<(PathBuf, Vec<u8>, u64)> {
        let mut files = inventory(&self.temp.0.join("legacy-meta"));
        for root in &self.settings.legacy.as_ref().unwrap().folders {
            files.extend(inventory(Path::new(&root.path)));
        }
        files
    }
}

#[tokio::test]
async fn playback_and_warming_reuse_multiple_legacy_roots_without_copying_after_restart() {
    let f = Fixture::new(true).await;
    let before = f.legacy_inventory();
    for _ in 0..2 {
        let cache = f.cache().await;
        assert!(!cache.can_fill(&f.vfs(cache.clone()).cache_key(&f.items[0]).await.unwrap()));
        let vfs = f.vfs(cache.clone());
        for item in &f.items {
            for delivery in [Delivery::WebDav, Delivery::Fuse] {
                let mut reader = vfs
                    .open(
                        &Node::Item(item.clone()),
                        Origin {
                            delivery,
                            client: "fixture".into(),
                            player: None,
                        },
                    )
                    .await
                    .unwrap()
                    .unwrap();
                let mut bytes = Vec::new();
                reader.read_to_end(&mut bytes).await.unwrap();
                assert_eq!(bytes, f.bytes);
                for offset in [0, BLOCK - 17, 2 * BLOCK + 10] {
                    reader.seek(std::io::SeekFrom::Start(offset)).await.unwrap();
                    let mut bytes = [0; 17];
                    reader.read_exact(&mut bytes).await.unwrap();
                    assert_eq!(
                        &bytes,
                        &f.bytes[usize::try_from(offset).unwrap()
                            ..usize::try_from(offset).unwrap() + 17]
                    );
                }
            }
        }
        let warming = infinidysk_prefetch::Queue::load(f.db.clone(), vfs.clone())
            .await
            .unwrap();
        for item in &f.items {
            let job = warming
                .enqueue(
                    item.id,
                    &[infinidysk_prefetch::Range {
                        start: 0,
                        end: f.bytes.len() as u64,
                    }],
                    40,
                    Uuid::new_v4(),
                )
                .await
                .unwrap();
            for _ in 0..5 {
                warming
                    .tick(&infinidysk_prefetch::Settings {
                        enabled: true,
                        ..Default::default()
                    })
                    .await
                    .unwrap();
            }
            let job = warming
                .status()
                .await
                .jobs
                .into_iter()
                .find(|j| j.id == job.id)
                .unwrap();
            assert_eq!(job.state, infinidysk_prefetch::State::Complete);
            assert_eq!(job.source_bytes, 0);
            assert_eq!(job.cached_bytes, f.bytes.len() as u64);
        }
        assert_eq!(
            cache.stats().await.unwrap().bytes,
            0,
            "no Rust payload copies"
        );
        assert_eq!(
            f.legacy_inventory(),
            before,
            "legacy contents and allocation stay unchanged"
        );
        drop(warming);
        drop(vfs);
        drop(cache);
    }
}

#[tokio::test]
async fn missing_provenance_and_changed_source_revision_cannot_hit_legacy_bytes() {
    let f = Fixture::new(false).await;
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    assert!(cache.get(&cache.lease(key).unwrap(), 0).await.is_none());
    drop(vfs);
    drop(cache);
    let f = Fixture::new(true).await;
    let cache = f.cache().await;
    let wrong = Key::new(f.items[0].id.0, b"changed source", f.bytes.len() as u64);
    assert!(cache.get(&cache.lease(wrong).unwrap(), 0).await.is_none());
    assert_eq!(cache.stats().await.unwrap().bytes, 0);
}

#[tokio::test]
async fn corruption_is_a_miss_without_rewriting_legacy_payloads() {
    let f = Fixture::new(true).await;
    let payload = inventory(Path::new(
        &f.settings.legacy.as_ref().unwrap().folders[0].path,
    ))
    .into_iter()
    .find(|(path, _, _)| path.file_name().unwrap() == "content.data")
    .unwrap()
    .0;
    std::fs::OpenOptions::new()
        .write(true)
        .open(&payload)
        .unwrap()
        .write_all_at(b"damaged", 0)
        .unwrap();
    let before = f.legacy_inventory();
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    assert!(cache.get(&cache.lease(key).unwrap(), 0).await.is_none());
    assert_eq!(f.legacy_inventory(), before);
    assert_eq!(cache.stats().await.unwrap().legacy_hits, 0);
}

#[tokio::test]
async fn handover_refuses_uncheckpointed_catalogue_and_repair_ledger() {
    let f = Fixture::new(true).await;
    let legacy = f.settings.legacy.as_ref().unwrap();
    for file in ["catalogue.db-wal", "repair-revisions.db-wal"] {
        let path = Path::new(&legacy.metadata_path).join(file);
        std::fs::write(&path, b"pending source transactions").unwrap();
        let before = f.legacy_inventory();
        assert!(LegacyReader::open(f.db.clone(), legacy).await.is_err());
        assert_eq!(f.legacy_inventory(), before);
        std::fs::remove_file(path).unwrap();
    }
}

#[tokio::test]
async fn legacy_handover_refuses_a_live_owner_and_releases_ownership_on_shutdown() {
    use nix::fcntl::{Flock, FlockArg};
    let f = Fixture::new(true).await;
    let legacy = f.settings.legacy.as_ref().unwrap();
    let owner = Path::new(&legacy.folders[0].path).join(".infinidysk-owner");
    let lock = Flock::lock(
        std::fs::File::open(&owner).unwrap(),
        FlockArg::LockExclusiveNonblock,
    )
    .unwrap();
    assert!(LegacyReader::open(f.db.clone(), legacy).await.is_err());
    drop(lock);
    let cache = f.cache().await;
    assert!(
        Flock::lock(
            std::fs::File::open(&owner).unwrap(),
            FlockArg::LockExclusiveNonblock
        )
        .is_err()
    );
    drop(cache);
    assert!(
        Flock::lock(
            std::fs::File::open(&owner).unwrap(),
            FlockArg::LockExclusiveNonblock
        )
        .is_ok()
    );
}

#[tokio::test]
#[ignore = "requires the original C# writer fixture receipt"]
async fn actual_csharp_payloads_serve_in_place_after_import_without_providers() {
    let receipt = std::env::var("INFINIDYSK_LEGACY_CACHE_FIXTURE").unwrap();
    let rows: Vec<serde_json::Value> =
        serde_json::from_slice(&std::fs::read(receipt).unwrap()).unwrap();
    let path = std::env::temp_dir().join(format!("id-real-in-place-{}", Uuid::new_v4()));
    std::fs::create_dir_all(path.join("source")).unwrap();
    let temp = Temp(path);
    let source = temp.0.join("source");
    let cs = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new()
            .filename(source.join("db.sqlite"))
            .create_if_missing(true),
    )
    .await
    .unwrap();
    sqlx::raw_sql("CREATE TABLE DavItems(Id TEXT, ParentId TEXT, Name TEXT, Path TEXT, CreatedAt TEXT, Type INTEGER, SubType INTEGER, FileSize INTEGER, FileBlobId TEXT, NzbBlobId TEXT, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER, HealthRepairPending INTEGER DEFAULT 0, GeneratedStrmPath TEXT, GeneratedSymlinkPath TEXT, ArrDownloadId TEXT);
        CREATE TABLE HistoryItems(Id TEXT, CreatedAt TEXT, FileName TEXT, JobName TEXT, Category TEXT, DownloadStatus INTEGER, TotalSegmentBytes INTEGER, DownloadTimeSeconds INTEGER, FailMessage TEXT, DownloadDirId TEXT, NzbBlobId TEXT);
        CREATE TABLE QueueItems(Id TEXT, CreatedAt TEXT, FileName TEXT, JobName TEXT, Category TEXT, Priority INTEGER, PostProcessing INTEGER, TotalSegmentBytes INTEGER, PauseUntil TEXT, SortOrder INTEGER);
        CREATE TABLE QueueNzbContents(Id TEXT, NzbContents TEXT);")
        .execute(&cs).await.unwrap();
    let category = Uuid::new_v4();
    let job = Uuid::new_v4();
    for (id, parent, name, path) in [
        (category, Uuid::from_u128(2), "", "/content"),
        (job, category, "fixture", "/content/fixture"),
    ] {
        sqlx::query("INSERT INTO DavItems(Id,ParentId,Name,Path,CreatedAt,Type,SubType) VALUES($1,$2,$3,$4,'2025-01-01',1,101)")
            .bind(id.to_string()).bind(parent.to_string()).bind(name).bind(path).execute(&cs).await.unwrap();
    }
    for row in &rows {
        let id = Uuid::parse_str(row["item"].as_str().unwrap()).unwrap();
        let blob = Uuid::parse_str(row["blob"].as_str().unwrap()).unwrap();
        let hex = blob.simple().to_string();
        let directory = source.join("blobs").join(&hex[..2]).join(&hex[2..4]);
        std::fs::create_dir_all(&directory).unwrap();
        // Only the small source metadata blob is copied. Cached media stays at its original path.
        std::fs::copy(
            Path::new(row["metadata_path"].as_str().unwrap()).join("source.blob"),
            directory.join(blob.to_string()),
        )
        .unwrap();
        let name = format!("{}.dat", row["mode"].as_str().unwrap());
        sqlx::query("INSERT INTO DavItems(Id,ParentId,Name,Path,CreatedAt,Type,SubType,FileSize,FileBlobId) VALUES($1,$2,$3,$4,'2025-01-01',2,$5,$6,$7)")
            .bind(id.to_string()).bind(job.to_string()).bind(&name).bind(format!("/content/fixture/{name}"))
            .bind(if row["multipart"].as_bool().unwrap() {203_i32}else{201_i32}).bind(row["length"].as_i64().unwrap()).bind(blob.to_string()).execute(&cs).await.unwrap();
    }
    cs.close().await;
    let db = Db::connect(&format!(
        "sqlite:{}",
        temp.0.join("target.sqlite").display()
    ))
    .await
    .unwrap();
    let client = Arc::new(Client::new([]));
    let imported = infinidysk_import::apply(&source, &db, &client, "/unused")
        .await
        .unwrap();
    assert_eq!(imported.files, 5);
    assert_eq!(imported.skipped_count, 0);
    assert_eq!(imported.fetched, 0);
    for row in &rows {
        let legacy = legacy::Settings {
            metadata_path: row["metadata_path"].as_str().unwrap().into(),
            folders: vec![legacy::Folder {
                id: row["folder_id"].as_str().unwrap().into(),
                path: row["folder_path"].as_str().unwrap().into(),
            }],
        };
        let before = inventory(Path::new(&legacy.metadata_path));
        let cache = Cache::open(Settings {
            enabled: true,
            io_timeout_ms: 10_000,
            metadata_path: temp
                .0
                .join(format!("meta-{}", row["mode"].as_str().unwrap()))
                .to_string_lossy()
                .into_owned(),
            legacy: Some(legacy.clone()),
            ..Settings::default()
        })
        .await
        .unwrap();
        cache
            .set_legacy_reader(LegacyReader::open(db.clone(), &legacy).await.unwrap())
            .unwrap();
        let vfs = Arc::new(
            Vfs::new(
                db.clone(),
                client.clone(),
                infinidysk_stream::Budget::new(8 * 1024 * 1024),
                1024 * 1024,
                "/unused",
            )
            .with_native_cache(Some(cache.clone())),
        );
        let item = db
            .item(ItemId(
                Uuid::parse_str(row["item"].as_str().unwrap()).unwrap(),
            ))
            .await
            .unwrap()
            .unwrap();
        let length = if row["mode"] == "partial" {
            BLOCK_BYTES
        } else {
            usize::try_from(row["length"].as_u64().unwrap()).unwrap()
        };
        for delivery in [Delivery::WebDav, Delivery::Fuse] {
            let mut reader = vfs
                .open(
                    &Node::Item(item.clone()),
                    Origin {
                        delivery,
                        client: "fixture".into(),
                        player: None,
                    },
                )
                .await
                .unwrap()
                .unwrap();
            let mut bytes = vec![0; length];
            reader.read_exact(&mut bytes).await.unwrap();
            assert!(
                bytes
                    .iter()
                    .enumerate()
                    .all(|(i, b)| *b == u8::try_from(i % 251).unwrap())
            );
            reader
                .seek(std::io::SeekFrom::Start(
                    u64::try_from(length - 37).unwrap(),
                ))
                .await
                .unwrap();
            let mut seek = [0; 37];
            reader.read_exact(&mut seek).await.unwrap();
            assert_eq!(seek, bytes[length - 37..]);
        }
        let warming = infinidysk_prefetch::Queue::load(db.clone(), vfs.clone())
            .await
            .unwrap();
        let job = warming
            .enqueue(
                item.id,
                &[infinidysk_prefetch::Range {
                    start: 0,
                    end: row["length"].as_u64().unwrap(),
                }],
                40,
                Uuid::new_v4(),
            )
            .await
            .unwrap();
        for _ in 0..5 {
            warming
                .tick(&infinidysk_prefetch::Settings {
                    enabled: true,
                    ..Default::default()
                })
                .await
                .unwrap();
        }
        let job = warming
            .status()
            .await
            .jobs
            .into_iter()
            .find(|j| j.id == job.id)
            .unwrap();
        assert_eq!(job.source_bytes, 0);
        assert_eq!(job.cached_bytes, length as u64);
        if row["mode"] == "partial" {
            assert_eq!(job.state, infinidysk_prefetch::State::Queued);
            assert!(job.reason.unwrap().contains("writable"));
        } else {
            assert_eq!(job.state, infinidysk_prefetch::State::Complete);
        }
        assert!(cache.stats().await.unwrap().legacy_hits > 0);
        assert_eq!(cache.stats().await.unwrap().bytes, 0);
        assert_eq!(
            inventory(Path::new(&legacy.metadata_path)),
            before,
            "C# cache and metadata must remain byte-identical"
        );
    }
}
