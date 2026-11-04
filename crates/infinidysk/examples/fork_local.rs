//! Disposable loopback-only fork canary. Run `cargo run -p infinidysk --example fork_local`.
//! All data is generated. The receipt contains paths/ids, never credentials.
use anyhow::Context as _;
use infinidysk::app::{Boot, serve};
use infinidysk::config::{AuthMode, ConfigStore, Provider, Secret, Settings};
use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, JobId, NzbFile, Segment, Segments, blob,
};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use infinidysk_native_cache::{FolderSettings, Settings as NativeSettings};
use infinidysk_testkit::{Corpus, LoopbackServer};
use sha2::Digest as _;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use tokio::net::TcpListener;
use tokio_util::sync::CancellationToken;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let arguments: Vec<_> = std::env::args().skip(1).collect();
    let resuming = arguments.first().is_some_and(|a| a == "--resume");
    anyhow::ensure!(
        arguments.is_empty() || resuming && arguments.len() == 2,
        "usage: fork_local [--resume GENERATED_ROOT]"
    );
    let root = if resuming {
        let root = std::fs::canonicalize(&arguments[1])?;
        anyhow::ensure!(
            root.starts_with(std::env::temp_dir())
                && root
                    .file_name()
                    .is_some_and(|n| n.to_string_lossy().starts_with("infinidysk-fork-local-"))
                && std::fs::read_to_string(root.join(".fork-local-fixture"))?
                    == "generated-seed-1025-v1",
            "resume accepts only a generated fork fixture"
        );
        let _ = std::fs::remove_file(root.join("stop"));
        root
    } else {
        let root = std::env::temp_dir().join(format!("infinidysk-fork-local-{}", ItemId::new()));
        std::fs::create_dir_all(root.join("payload"))?;
        std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700))?;
        std::fs::write(root.join(".fork-local-fixture"), "generated-seed-1025-v1")?;
        root
    };
    let database_url = format!("sqlite:{}", root.join("infinidysk.sqlite").display());
    let corpus = Corpus::generate(1025, 32, 256 * 1024);
    let expected_hash = sha2::Sha256::digest(&corpus.data);
    let server = LoopbackServer::start(corpus.articles.clone()).await?;
    let db = Db::connect(&database_url).await?;
    if !resuming && let Ok(config) = std::env::var("INFINIDYSK_LEGACY_FIXTURE_CONFIG") {
        // This explicit opt-in accepts only a generated local fixture, never production.
        let config = std::path::Path::new(&config);
        anyhow::ensure!(
            config.starts_with(std::env::temp_dir())
                && config
                    .file_name()
                    .is_some_and(|name| name.to_string_lossy().starts_with("infinidysk-import-")),
            "expected a generated import fixture"
        );
        let source = infinidysk_nntp::Client::new(Vec::<infinidysk_nntp::Provider>::new());
        let imported = infinidysk_import::apply(config, &db, &source, "/unused").await?;
        anyhow::ensure!(imported.skipped_count == 0, "fixture import skipped files");
    }

    let file = if resuming {
        let receipt: serde_json::Value =
            serde_json::from_slice(&std::fs::read(root.join("receipt.json"))?)?;
        let id = receipt["item"]
            .as_str()
            .context("fixture item id")?
            .parse()?;
        db.item(ItemId(id)).await?.context("fixture source")?
    } else {
        let content = db.item(ItemId::CONTENT).await?.context("content root")?;
        let category = Item::child(&content, "fixture", ItemKind::Directory)?;
        let job = Item::child(&category, "job", ItemKind::Directory)?;
        let mut file = Item::child(&job, "fixture.bin", ItemKind::NzbFile)?;
        let blob_id = BlobId::new();
        file.meta_blob = Some(blob_id);
        file.size = Some(corpus.data.len() as u64);
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
        db.insert(std::slice::from_ref(&category), &[]).await?;
        let history = HistoryEntry {
            id: JobId::new(),
            created_at: 1,
            file_name: "fixture.nzb".to_owned(),
            job_name: "job".to_owned(),
            category: "fixture".to_owned(),
            status: JobStatus::Completed,
            total_bytes: file.size.unwrap_or(0),
            download_seconds: 0,
            fail_message: None,
            download_dir: Some(job.id),
            nzb_blob: None,
            storage: None,
        };
        db.add_history(
            &history,
            &[job, file.clone()],
            &[(blob_id, &blob::encode_meta(&meta)?)],
        )
        .await?;
        file
    };
    let mut settings = if resuming {
        ConfigStore::load(db.clone(), std::iter::empty())
            .await?
            .settings()
            .clone()
    } else {
        Settings::default()
    };
    settings.auth.mode = AuthMode::Disabled;
    settings.mount.dir = root.join("mount").to_string_lossy().into_owned();
    "fixture".clone_into(&mut settings.webdav.user);
    settings.webdav.password = Secret("fixture".to_owned());
    settings.health.enabled = false;
    settings.health.media_probe = false;
    settings.health.auto_repair = false;
    settings.providers = vec![Provider {
        name: "loopback-only".to_owned(),
        hostname: "127.0.0.1".to_owned(),
        port: server.addr().port(),
        tls: false,
        max_connections: 4,
        pipeline_depth: 4,
        ..Provider::default()
    }];
    if resuming {
        anyhow::ensure!(
            std::path::Path::new(&settings.native_cache.metadata_path).starts_with(&root)
                && !std::path::Path::new(&settings.native_cache.metadata_path)
                    .components()
                    .any(|c| c == std::path::Component::ParentDir)
                && settings
                    .native_cache
                    .folders
                    .iter()
                    .all(|f| std::path::Path::new(&f.path).starts_with(&root)
                        && !std::path::Path::new(&f.path)
                            .components()
                            .any(|c| c == std::path::Component::ParentDir)),
            "fixture cache must stay inside its generated root"
        );
        settings.prefetch.enabled = false;
        settings.prefetch.plex.enabled = false;
        settings.prefetch.plex.sources.clear();
        settings.plex.accounts.clear();
        settings.plex.servers.clear();
        settings.arr_instances.clear();
        settings.imports.library_dirs.clear();
        settings.imports.scan_dirs.clear();
    } else {
        settings.native_cache = NativeSettings {
            enabled: true,
            min_file_mb: 0,
            metadata_path: root.join("cache-metadata").to_string_lossy().into_owned(),
            folders: vec![FolderSettings {
                id: ItemId::new().0,
                path: root.join("payload").to_string_lossy().into_owned(),
                min_free_bytes: 0,
                ..FolderSettings::default()
            }],
            ..NativeSettings::default()
        };
    }
    ConfigStore::load(db.clone(), std::iter::empty())
        .await?
        .save(settings)
        .await?;
    db.close().await;
    let listener = TcpListener::bind("127.0.0.1:0").await?;
    let address = listener.local_addr()?;

    let digest: String = expected_hash
        .iter()
        .flat_map(|b| {
            const HEX: &[u8; 16] = b"0123456789abcdef";
            [
                char::from(HEX[usize::from(b >> 4)]),
                char::from(HEX[usize::from(b & 15)]),
            ]
        })
        .collect();
    let receipt = serde_json::json!({"url":format!("http://{address}"),"root":root,"item":file.id.0,"source_path":file.path,"size":file.size,"sha256":digest});
    let receipt_path = root.join("receipt.json");
    std::fs::write(&receipt_path, serde_json::to_vec_pretty(&receipt)?)?;
    std::fs::set_permissions(&receipt_path, std::fs::Permissions::from_mode(0o600))?;
    println!("{}", receipt_path.display());
    let owner = std::fs::metadata(&root)?;
    let boot = Boot {
        listen: address,
        database_url,
        uid: owner.uid(),
        gid: owner.gid(),
        trace_minutes: 0,
        metrics_path: root.join("metrics.sqlite"),
        log_dir: root.join("logs"),
        config_dir: root.clone(),
        ffprobe: "ffprobe".to_owned(),
    };
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    let flag = root.join("stop");
    let signal = tokio::spawn(async move {
        loop {
            if flag.exists() {
                stop.cancel();
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
    });
    let result = serve(listener, &boot, std::iter::empty(), cancel).await;
    signal.abort();
    drop(server);
    result?;
    Ok(())
}
