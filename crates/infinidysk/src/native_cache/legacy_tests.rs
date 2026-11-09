//! No-copy compatibility through the actual application bridge, VFS and warming queue.
#![allow(clippy::unwrap_used)] // Generated fixtures; every failure is an assertion.
use super::LegacyReader;
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, NzbFile, Segment, Segments, blob};
use infinidysk_db::{Db, HealthStatus, Item};
use infinidysk_import::native::{Ledger, Source};
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, Key, Settings, legacy};
use infinidysk_nntp::Client;
use infinidysk_testkit::{Corpus, LoopbackServer};
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
        let path = std::env::temp_dir()
            .canonicalize()
            .unwrap()
            .join(format!("id-in-place-{}", Uuid::new_v4()));
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
        sqlx::raw_sql(include_str!(
            "../../../../scripts/fixtures/legacy-native-cache/catalogue-schema.sql"
        ))
        .execute(&catalogue)
        .await
        .unwrap();
        let revisions = sqlx::SqlitePool::connect_with(options("repair-revisions.db"))
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE Identity(Id TEXT); INSERT INTO Identity VALUES('fixture-epoch'); CREATE TABLE Revisions(Hash TEXT,Stamp TEXT);")
            .execute(&revisions).await.unwrap();
        revisions.close().await;
        let ledger = Ledger::open(&temp.0.join("legacy-meta/repair-revisions.db"))
            .await
            .unwrap();
        let corpus = Corpus::generate(1025, 33, 256 * 1024);
        let bytes = corpus.data.clone();
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
                    device_parts(stat.dev()).0,
                    device_parts(stat.dev()).1,
                    stat.ino()
                ))
                .execute(&catalogue)
                .await
                .unwrap();
            folders.push(legacy::Folder {
                owner_lock_path: None,
                id: folder.clone(),
                path: root.to_string_lossy().into_owned(),
            });
            let mut item =
                Item::child(&parent, &format!("fixture-{index}.dat"), ItemKind::NzbFile).unwrap();
            let meta = FileMeta::Nzb(NzbFile {
                segments: Segments {
                    items: corpus
                        .articles
                        .iter()
                        .map(|a| Segment {
                            id: a.message_id.clone(),
                            size: 256 * 1024,
                            fallbacks: vec![],
                        })
                        .collect(),
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
            sqlx::query("INSERT INTO Entries(Key,Folder,ItemId,Length,Pinned,Generation,ChunkSize,Bytes,Access) VALUES($1,$2,$3,$4,1,$5,$6,$4+65536,0)")
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
                write: None,
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
async fn application_maintenance_evicts_only_opted_in_idle_legacy_entries() {
    let mut f = Fixture::new(true).await;
    let legacy = f.settings.legacy.as_mut().unwrap();
    legacy.write = Some(legacy::WriteSettings {
        chunk_mb: 8,
        folders: legacy
            .folders
            .iter()
            .map(|folder| legacy::WriteFolder {
                id: folder.id.clone(),
                priority: 0,
                quota_bytes: 100 * BLOCK,
                min_free_bytes: 0,
                eviction: Some(legacy::EvictionPolicy {
                    max_age_days: 1,
                    high_percent: 90,
                    low_percent: 80,
                }),
            })
            .collect(),
    });
    let catalogue = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new().filename(f.temp.0.join("legacy-meta/catalogue.db")),
    )
    .await
    .unwrap();
    sqlx::query("UPDATE Entries SET Pinned=0 WHERE ItemId=$1")
        .bind(f.items[0].id.0.simple().to_string())
        .execute(&catalogue)
        .await
        .unwrap();
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    let lease = cache.lease(key.clone()).unwrap();
    cache.maintain().await.unwrap();
    assert!(!cache.coverage(&key).await.unwrap().is_empty());
    drop(lease);
    cache.maintain().await.unwrap();
    assert!(cache.coverage(&key).await.unwrap().is_empty());
    let entries: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM Entries")
        .fetch_one(&catalogue)
        .await
        .unwrap();
    assert_eq!(entries, 1);
    let mut reader = vfs
        .open(
            &Node::Item(f.items[1].clone()),
            Origin {
                delivery: Delivery::WebDav,
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
    cache.flush_legacy_recency().await.unwrap();
    assert_eq!(cache.evictions(None, 100).await.unwrap().len(), 1);
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
async fn compatible_warming_fills_partial_and_new_sources_then_reuses_them_without_providers() {
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use std::time::Duration;
    let mut f = Fixture::new(true).await;
    let source = f.settings.legacy.as_ref().unwrap();
    let catalogue = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new().filename(Path::new(&source.metadata_path).join("catalogue.db")),
    )
    .await
    .unwrap();
    let key: String = sqlx::query_scalar("SELECT Key FROM Entries WHERE ItemId=$1")
        .bind(f.items[0].id.0.simple().to_string())
        .fetch_one(&catalogue)
        .await
        .unwrap();
    sqlx::query("DELETE FROM Blocks WHERE Key=$1 AND Offset=$2")
        .bind(key)
        .bind(BLOCK.cast_signed())
        .execute(&catalogue)
        .await
        .unwrap();
    catalogue.close().await;
    let original: Vec<_> = f
        .legacy_inventory()
        .into_iter()
        .filter(|(p, _, _)| {
            p.extension().is_some_and(|e| e == "data") || p.file_name().unwrap() == "manifest.json"
        })
        .map(|(path, hash, _)| {
            let metadata = path.metadata().unwrap();
            (path, hash, (metadata.dev(), metadata.ino(), metadata.len()))
        })
        .collect();
    f.settings.min_file_mb = 0;
    let source = f.settings.legacy.as_mut().unwrap();
    source.write = Some(legacy::WriteSettings {
        chunk_mb: 8,
        folders: source
            .folders
            .iter()
            .map(|folder| legacy::WriteFolder {
                eviction: None,
                id: folder.id.clone(),
                priority: 0,
                quota_bytes: 1_000_000_000,
                min_free_bytes: 0,
            })
            .collect(),
    });
    let corpus = Corpus::generate(1025, 33, 256 * 1024);
    assert_eq!(f.bytes, corpus.data);
    let server = LoopbackServer::start(corpus.articles).await.unwrap();
    let pool = Pool::new(
        Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(3),
        )
        .unwrap(),
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            min_connections: 0,
            idle_timeout: Duration::from_secs(5),
        },
        Downloads::new(8),
    )
    .unwrap();
    let cache = f.cache().await;
    let vfs = Arc::new(
        Vfs::new(
            f.db.clone(),
            Arc::new(Client::new([Provider {
                pool,
                key: "fixture".into(),
                name: "fixture".into(),
                priority: 0,
                backbone: None,
            }])),
            infinidysk_stream::Budget::new(8 * 1024 * 1024),
            1024 * 1024,
            "/unused",
        )
        .with_native_cache(Some(cache.clone())),
    );
    let root = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut new = Item::child(&root, "new-fixture.dat", ItemKind::NzbFile).unwrap();
    new.size = Some(f.bytes.len() as u64);
    new.meta_blob = Some(BlobId::new());
    let metadata =
        f.db.blob(f.items[0].meta_blob.unwrap())
            .await
            .unwrap()
            .unwrap();
    f.db.insert(
        std::slice::from_ref(&new),
        &[(new.meta_blob.unwrap(), &metadata)],
    )
    .await
    .unwrap();
    let warming = infinidysk_prefetch::Queue::load(f.db.clone(), vfs.clone())
        .await
        .unwrap();
    for (item, expected_source) in [(&f.items[0], BLOCK), (&new, f.bytes.len() as u64)] {
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
        for _ in 0..10 {
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
        assert_eq!(
            job.state,
            infinidysk_prefetch::State::Complete,
            "{:?}",
            job.reason
        );
        assert_eq!(job.source_bytes, expected_source);
        assert_eq!(job.cached_bytes + job.source_bytes, f.bytes.len() as u64);
        let key = vfs.cache_key(item).await.unwrap();
        let intent = Uuid::new_v4();
        let scope = "a".repeat(64);
        let ranges = [infinidysk_prefetch::Range {
            start: 0,
            end: f.bytes.len() as u64,
        }];
        let request = || infinidysk_prefetch::Automatic {
            item: item.id,
            ranges: &ranges,
            priority: 40,
            intent,
            revision: key.revision(),
            scope: &scope,
        };
        let automatic = warming.enqueue_automatic(request()).await.unwrap();
        let before = server.body_commands();
        for _ in 0..5 {
            warming
                .tick(&infinidysk_prefetch::Settings {
                    enabled: true,
                    ..Default::default()
                })
                .await
                .unwrap();
        }
        let repeated = warming.enqueue_automatic(request()).await.unwrap();
        assert_eq!(automatic.id, repeated.id);
        assert_eq!(repeated.state, infinidysk_prefetch::State::Complete);
        assert_eq!(repeated.source_bytes, 0);
        assert_eq!(server.body_commands(), before);
    }
    assert_eq!(
        cache.stats().await.unwrap().legacy_written_bytes,
        BLOCK + f.bytes.len() as u64
    );
    for (path, hash, identity) in original {
        assert_eq!(Sha256::digest(std::fs::read(&path).unwrap()).to_vec(), hash);
        let metadata = path.metadata().unwrap();
        assert_eq!(
            (metadata.dev(), metadata.ino(), metadata.len()),
            identity,
            "existing payload must keep its inode, device and length"
        );
    }
    assert!(server.body_commands() > 0);
    drop(warming);
    drop(vfs);
    drop(cache);
    drop(server);
    let cache = f.cache().await;
    let vfs = f.vfs(cache);
    for item in [&f.items[0], &new] {
        let mut reader = vfs
            .open(
                &Node::Item(item.clone()),
                Origin {
                    delivery: Delivery::WebDav,
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
    }
}

async fn pins(f: &Fixture, value: Option<i64>) -> Vec<i64> {
    let db = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new().filename(f.temp.0.join("legacy-meta/catalogue.db")),
    )
    .await
    .unwrap();
    if let Some(value) = value {
        sqlx::query("UPDATE Entries SET Pinned=$1")
            .bind(value)
            .execute(&db)
            .await
            .unwrap();
    }
    let result = sqlx::query_scalar("SELECT Pinned FROM Entries ORDER BY ItemId")
        .fetch_all(&db)
        .await
        .unwrap();
    db.close().await;
    result
}
fn enable_writes(f: &mut Fixture) {
    f.settings.min_file_mb = 0;
    let source = f.settings.legacy.as_mut().unwrap();
    source.write = Some(legacy::WriteSettings {
        chunk_mb: 8,
        folders: source
            .folders
            .iter()
            .map(|folder| legacy::WriteFolder {
                eviction: None,
                id: folder.id.clone(),
                priority: 0,
                quota_bytes: 1_000_000_000,
                min_free_bytes: 0,
            })
            .collect(),
    });
}

#[tokio::test]
async fn legacy_coverage_protects_complete_copies_and_preserves_pins_during_storage_uncertainty() {
    let mut f = Fixture::new(true).await;
    pins(&f, Some(0)).await;
    enable_writes(&mut f);
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    let ranges = cache.coverage(&key).await.unwrap();
    assert_eq!(ranges.len(), 1);
    assert_eq!(ranges[0].end, f.bytes.len() as u64);
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), Some(true));
    assert!(pins(&f, None).await.contains(&2));
    let marker =
        Path::new(&f.settings.legacy.as_ref().unwrap().folders[0].path).join(".infinidysk-volume");
    let original = std::fs::read(&marker).unwrap();
    std::fs::write(&marker, b"unavailable volume").unwrap();
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), None);
    assert!(pins(&f, None).await.contains(&2));
    std::fs::write(marker, original).unwrap();
    let payload = inventory(Path::new(
        &f.settings.legacy.as_ref().unwrap().folders[0].path,
    ))
    .into_iter()
    .find(|(p, _, _)| p.file_name().unwrap() == "content.data")
    .unwrap()
    .0;
    std::fs::OpenOptions::new()
        .write(true)
        .open(&payload)
        .unwrap()
        .write_all_at(b"corrupt", 0)
        .unwrap();
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), Some(false));
    assert!(!pins(&f, None).await.contains(&2));
    assert!(
        cache
            .put(
                &cache.lease(key.clone()).unwrap(),
                0,
                axum::body::Bytes::copy_from_slice(&f.bytes[..BLOCK_BYTES])
            )
            .await
            .unwrap()
    );
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), Some(true));
    pins(&f, Some(1)).await;
    assert!(!cache.release_protection(&key).await.unwrap());
    assert_eq!(pins(&f, None).await, vec![1, 1]);
}

#[tokio::test]
async fn read_only_copies_need_existing_durable_pins_before_destructive_recovery() {
    let f = Fixture::new(true).await;
    pins(&f, Some(0)).await;
    let before = f.legacy_inventory();
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), None);
    assert_eq!(f.legacy_inventory(), before);
}

#[tokio::test]
async fn unresolved_publications_hold_retained_copy_decisions_without_hiding_committed_hits() {
    let f = Fixture::new(true).await;
    pins(&f, Some(2)).await;
    let db = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new().filename(f.temp.0.join("legacy-meta/catalogue.db")),
    )
    .await
    .unwrap();
    sqlx::query("DELETE FROM Blocks WHERE Offset=$1")
        .bind(BLOCK.cast_signed())
        .execute(&db)
        .await
        .unwrap();
    sqlx::query("UPDATE Entries SET PendingBytes=$1,Dirty=1")
        .bind((BLOCK + 4096).cast_signed())
        .execute(&db)
        .await
        .unwrap();
    db.close().await;
    let cache = f.cache().await;
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    assert!(cache.verify_and_protect(&key).await.is_err());
    assert_eq!(pins(&f, None).await, vec![2, 2]);
    assert_eq!(
        cache
            .get(&cache.lease(key).unwrap(), 0)
            .await
            .unwrap()
            .as_ref(),
        &f.bytes[..BLOCK_BYTES]
    );
}

#[tokio::test]
async fn mixed_rust_and_legacy_coverage_requires_both_copies_to_be_protected() {
    let mut f = Fixture::new(true).await;
    pins(&f, Some(0)).await;
    enable_writes(&mut f);
    let db = sqlx::SqlitePool::connect_with(
        SqliteConnectOptions::new().filename(f.temp.0.join("legacy-meta/catalogue.db")),
    )
    .await
    .unwrap();
    sqlx::query("DELETE FROM Blocks WHERE Offset=0")
        .execute(&db)
        .await
        .unwrap();
    db.close().await;
    let path = f.temp.0.join("rust-payload");
    std::fs::create_dir(&path).unwrap();
    f.settings
        .folders
        .push(infinidysk_native_cache::FolderSettings {
            id: Uuid::new_v4(),
            path: path.to_string_lossy().into_owned(),
            quota_bytes: 1_000_000_000,
            min_free_bytes: 0,
            ..Default::default()
        });
    let cache = Cache::open(f.settings.clone()).await.unwrap();
    let vfs = f.vfs(cache.clone());
    let key = vfs.cache_key(&f.items[0]).await.unwrap();
    assert!(
        cache
            .put(
                &cache.lease(key.clone()).unwrap(),
                0,
                axum::body::Bytes::copy_from_slice(&f.bytes[..BLOCK_BYTES])
            )
            .await
            .unwrap()
    );
    cache
        .set_legacy_reader(
            LegacyReader::open(f.db.clone(), f.settings.legacy.as_ref().unwrap())
                .await
                .unwrap(),
        )
        .unwrap();
    assert_eq!(cache.verify_and_protect(&key).await.unwrap(), Some(true));
    assert!(cache.entries(0, 10).await.unwrap()[0].pinned);
    assert!(pins(&f, None).await.contains(&2));
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
    let path = std::env::temp_dir()
        .canonicalize()
        .unwrap()
        .join(format!("id-real-in-place-{}", Uuid::new_v4()));
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
            write: None,
            metadata_path: row["metadata_path"].as_str().unwrap().into(),
            folders: vec![legacy::Folder {
                owner_lock_path: None,
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

// Construct native device registration independently of the reader under test.
fn device_parts(device: u64) -> (u64, u64) {
    #[cfg(target_os = "macos")]
    {
        ((device >> 24) & 0xff, device & 0x00ff_ffff)
    }
    #[cfg(not(target_os = "macos"))]
    {
        (nix::sys::stat::major(device), nix::sys::stat::minor(device))
    }
}
