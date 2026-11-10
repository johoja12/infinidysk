use std::os::unix::process::CommandExt as _;
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use anyhow::Context as _;
use infinidysk::app::{self, Boot, Exit};
use infinidysk::logs::{self, Capture};
use tokio::net::TcpListener;
use tokio::signal::unix::{SignalKind, signal};
use tokio_util::sync::CancellationToken;
use tracing_subscriber::layer::SubscriberExt as _;
use tracing_subscriber::util::SubscriberInitExt as _;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    if std::env::args().nth(1).as_deref() == Some("openapi") {
        println!("{}", infinidysk::admin::openapi_json()?);
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("import-report") {
        let home = app::home_from_env()?;
        let config = std::env::args()
            .nth(2)
            .map_or_else(|| home.clone(), std::path::PathBuf::from);
        let default_cache_dir = home.join("cache").display().to_string();
        eprintln!(
            "Reading the 1.x install in {}. A large install takes a few minutes; the report prints when done.",
            config.display()
        );
        let lines = infinidysk::upgrade::ReportProgress::default();
        let progress = |p| {
            if let Some(line) = lines.line(p, std::time::Instant::now()) {
                eprintln!("{line}");
            }
        };
        let report = infinidysk::upgrade::report(&config, &default_cache_dir, progress).await?;
        print!("{report}");
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("fork-pg-snapshot") {
        use std::io::Read as _;
        let args: Vec<_> = std::env::args().skip(2).collect();
        anyhow::ensure!(
            args.len() == 2,
            "usage: infinidysk fork-pg-snapshot NEW_DIRECTORY SOURCE_IANA_TIMEZONE < PRIVATE_CONNECTION_FILE"
        );
        let mut connection = String::new();
        std::io::stdin()
            .take(16385)
            .read_to_string(&mut connection)?;
        anyhow::ensure!(
            connection.len() <= 16384,
            "connection settings exceed input bound"
        );
        let receipt =
            infinidysk_import::postgres::snapshot(&connection, args[0].as_ref(), &args[1]).await?;
        println!(
            "legacy PostgreSQL snapshot: {} files, digest {}, optional metadata cells held {}",
            receipt.files.len(),
            receipt.digest,
            receipt.quarantined_cells
        );
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("fork-pg-verify") {
        let args: Vec<_> = std::env::args().skip(2).collect();
        anyhow::ensure!(
            args.len() == 2,
            "usage: infinidysk fork-pg-verify DIRECTORY EXPECTED_DIGEST"
        );
        let receipt = infinidysk_import::postgres::verify(args[0].as_ref())?;
        anyhow::ensure!(
            receipt.digest == args[1],
            "PostgreSQL snapshot digest differs from the export receipt"
        );
        println!(
            "legacy PostgreSQL snapshot verified: {} files, digest {}, optional metadata cells held {}",
            receipt.files.len(),
            receipt.digest,
            receipt.quarantined_cells
        );
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("fork-export") {
        let args: Vec<_> = std::env::args_os()
            .skip(2)
            .map(std::path::PathBuf::from)
            .collect();
        anyhow::ensure!(
            args.len() >= 2,
            "usage: infinidysk fork-export CONFIG NEW_PACKAGE [EXTRA_JOURNAL ...]"
        );
        let manifest = infinidysk_import::fork::export(
            &args[0],
            &args[1],
            &args[2..],
            std::env::vars().collect(),
        )
        .await?;
        println!(
            "legacy evidence package: {} files, digest {}",
            manifest.entries.len(),
            manifest.digest
        );
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("fork-verify") {
        let args: Vec<_> = std::env::args_os()
            .skip(2)
            .map(std::path::PathBuf::from)
            .collect();
        anyhow::ensure!(
            args.len() == 2,
            "usage: infinidysk fork-verify PACKAGE EXPECTED_DIGEST"
        );
        let manifest = infinidysk_import::fork::verify(&args[0])?;
        anyhow::ensure!(
            args[1].to_str() == Some(manifest.digest.as_str()),
            "legacy evidence digest does not match the expected export receipt"
        );
        println!(
            "legacy evidence verified: {} files, digest {}",
            manifest.entries.len(),
            manifest.digest
        );
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("healthcheck") {
        let listen = infinidysk::app::listen_from_env()?;
        match infinidysk::healthcheck::probe(listen).await {
            Ok(body) => println!("{body}"),
            // An expected answer, not a crash: one line, no backtrace.
            Err(error) => {
                eprintln!("unhealthy: {error:#}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    // Checked before anything reads it, so a folder infinidysk cannot use stops with one line.
    let setting = match app::config_dir_from_env() {
        Ok(setting) => setting,
        Err(error) => {
            tracing_subscriber::fmt().init();
            let reason = format!("{error:#}");
            tracing::warn!(
                %reason,
                "cannot use INFINIDYSK_CONFIG_DIR; fix the setting or the folder's permissions, then restart"
            );
            std::process::exit(1);
        }
    };
    let boot = Boot::from_env(setting.as_deref())?;
    let (capture, unkept) = match Capture::persisted(&boot.log_dir, (boot.uid, boot.gid)) {
        Ok(capture) => (capture, None),
        Err(error) => (Capture::MEMORY, Some(error)),
    };
    let filter = logs::filter(std::env::var("RUST_LOG").ok().as_deref());
    tracing_subscriber::registry()
        .with(filter)
        .with(tracing_subscriber::fmt::layer())
        .with(capture)
        .init();
    tracing::info!(dir = %boot.home.display(), "using the config folder");
    if let Some(error) = unkept {
        tracing::warn!(
            dir = %boot.log_dir.display(),
            reason = %error,
            "logs are not kept across restarts: directory is not writable"
        );
    }
    let listener = TcpListener::bind(boot.listen)
        .await
        .with_context(|| format!("listening on {}", boot.listen))?;
    let cancel = CancellationToken::new();
    let mut term = signal(SignalKind::terminate()).context("installing the SIGTERM handler")?;
    let stop = cancel.clone();
    let signalled = Arc::new(AtomicBool::new(false));
    let signal_seen = Arc::clone(&signalled);
    tokio::spawn(async move {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = term.recv() => {}
        }
        tracing::info!("shutting down");
        signal_seen.store(true, Ordering::Relaxed);
        stop.cancel();
    });
    let exit = match app::serve(listener, &boot, std::env::vars(), cancel).await {
        Ok(exit) => exit,
        // Invalid saved settings are a known failure: one line, no backtrace.
        Err(error)
            if matches!(
                error.downcast_ref::<infinidysk::config::Error>(),
                Some(infinidysk::config::Error::Invalid { .. })
            ) =>
        {
            let reason = format!("{error:#}");
            let message = "the saved settings are invalid; fix them or override them with INFINIDYSK__ variables, then restart";
            // A stricter RUST_LOG must not hide why startup stopped.
            if tracing::enabled!(tracing::Level::WARN) {
                tracing::warn!(%reason, "{message}");
            } else {
                eprintln!("{message}: {reason}");
            }
            std::process::exit(1);
        }
        Err(error) => return Err(error),
    };
    // A stop signal during a restart wins: the container is being stopped.
    if exit == Exit::Restart && !signalled.load(Ordering::Relaxed) {
        let exe = std::env::current_exe().context("finding the binary to restart")?;
        tracing::info!("starting again");
        let error = Command::new(exe).args(std::env::args_os().skip(1)).exec();
        return Err(error).context("restarting");
    }
    Ok(())
}
