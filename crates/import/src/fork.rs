//! Private migration evidence. Preserves legacy state; never activates a legacy job.
//! SQLite snapshots include committed WAL data. Cross-file consistency still requires
//! a stopped 1.x installation; source content witnesses reject observable changes during export.
use std::fmt::Write as _;
use std::{
    fs::{self, File, OpenOptions},
    io::{Read as _, Write as _},
    os::unix::fs::{
        DirBuilderExt as _, MetadataExt as _, OpenOptionsExt as _, PermissionsExt as _,
    },
    path::{Path, PathBuf},
};

use nix::fcntl::OFlag;
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use sqlx::{ConnectOptions as _, Connection as _, sqlite::SqliteConnectOptions};
use uuid::Uuid;

const MAX_FILES: usize = 64;
const MAX_DATABASE_BYTES: u64 = 4 * 1024 * 1024 * 1024;
const MAX_FILE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_MANIFEST_BYTES: u64 = 128 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("legacy state export I/O: {0}")]
    Io(#[from] std::io::Error),
    #[error("legacy state snapshot failed")]
    Database(#[from] sqlx::Error),
    #[error("legacy state encoding failed")]
    Json(#[from] serde_json::Error),
    #[error("legacy state settings could not be read")]
    Config(#[from] crate::Error),
    #[error("{0}")]
    Invalid(&'static str),
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Entry {
    /// Numeric package-local filename, never a source-provided path.
    pub file: String,
    pub source: String,
    pub kind: String,
    pub bytes: u64,
    pub sha256: String,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Manifest {
    pub version: u8,
    pub id: Uuid,
    pub entries: Vec<Entry>,
    /// Digest of the version/id/entries tuple. No credentials are printed by CLI.
    pub digest: String,
}
#[derive(Clone, Debug, PartialEq, Eq)]
struct Stamp {
    dev: u64,
    inode: u64,
    bytes: u64,
    modified: (i64, i64),
    changed: (i64, i64),
}
fn stamp(path: &Path) -> Result<Option<Stamp>, Error> {
    match fs::symlink_metadata(path) {
        Ok(m) if m.is_file() && m.nlink() == 1 => Ok(Some(Stamp {
            dev: m.dev(),
            inode: m.ino(),
            bytes: m.len(),
            modified: (m.mtime(), m.mtime_nsec()),
            changed: (m.ctime(), m.ctime_nsec()),
        })),
        Ok(_) => Err(Error::Invalid(
            "legacy state must be a private regular file",
        )),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e.into()),
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
struct Witness {
    stamp: Stamp,
    sha256: String,
}
fn witness(path: &Path) -> Result<Option<Witness>, Error> {
    let Some(before) = stamp(path)? else {
        return Ok(None);
    };
    let (_, sha256) = hash(path, MAX_DATABASE_BYTES)?;
    if stamp(path)?.as_ref() != Some(&before) {
        return Err(Error::Invalid(
            "legacy state changed while reading export evidence",
        ));
    }
    Ok(Some(Witness {
        stamp: before,
        sha256,
    }))
}
fn input(path: &Path) -> Result<File, Error> {
    let file = OpenOptions::new()
        .read(true)
        .custom_flags((OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK).bits())
        .open(path)?;
    let m = file.metadata()?;
    if !m.is_file() || m.nlink() != 1 {
        return Err(Error::Invalid(
            "legacy state must be a private regular file",
        ));
    }
    Ok(file)
}
fn output(path: &Path) -> Result<File, Error> {
    Ok(OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(path)?)
}
fn hash(path: &Path, limit: u64) -> Result<(u64, String), Error> {
    let mut file = input(path)?.take(limit + 1);
    let mut digest = Sha256::new();
    let mut size = 0;
    let mut buf = vec![0; 64 * 1024].into_boxed_slice();
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        size += n as u64;
        if size > limit {
            return Err(Error::Invalid("legacy state exceeds export bound"));
        }
        digest.update(&buf[..n]);
    }
    Ok((size, hex(&digest.finalize())))
}
fn hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .fold(String::with_capacity(bytes.len() * 2), |mut out, b| {
            write!(out, "{b:02x}").expect("formatting into String cannot fail");
            out
        })
}
fn digest(manifest: &Manifest) -> Result<String, Error> {
    Ok(hex(&Sha256::digest(serde_json::to_vec(&(
        manifest.version,
        manifest.id,
        &manifest.entries,
    ))?)))
}
fn sqlite(path: &Path) -> Result<bool, Error> {
    let mut file = input(path)?;
    if file.metadata()?.len() < 16 {
        return Ok(false);
    }
    let mut header = [0; 16];
    file.read_exact(&mut header)?;
    Ok(&header == b"SQLite format 3\0")
}
struct Incomplete(PathBuf);
impl Drop for Incomplete {
    fn drop(&mut self) {
        if !self.0.as_os_str().is_empty() {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
}

/// Exports a stopped installation's settings/databases and known fork journals.
/// Extra paths explicitly attach operational runner journals outside `/config`.
/// Destination must not exist. Payload caches and NZB blobs are not copied.
pub async fn export(
    config: &Path,
    destination: &Path,
    extras: &[PathBuf],
    vars: Vec<(String, String)>,
) -> Result<Manifest, Error> {
    if vars.iter().any(|(key, value)| {
        key == "DATABASE_PROVIDER"
            && !value.trim().is_empty()
            && !value.trim().eq_ignore_ascii_case("sqlite")
    }) {
        return Err(Error::Invalid(
            "this export supports legacy SQLite only; preserve a PostgreSQL source with a database-native backup",
        ));
    }
    if extras.len() > 48 {
        return Err(Error::Invalid("too many operational journal attachments"));
    }
    let config = fs::canonicalize(config)?;
    let database = config.join("db.sqlite");
    if stamp(&database)?.is_none() {
        return Err(Error::Invalid("legacy source database is missing"));
    }
    let cs = crate::Config::read(&config, vars.clone()).await?;
    let metadata = cs
        .get("cache.native.metadata-path")
        .filter(|s| !s.is_empty())
        .map_or_else(|| config.join("native-cache-metadata"), PathBuf::from);
    if !metadata.is_absolute() {
        return Err(Error::Invalid("legacy metadata path must be absolute"));
    }
    let mut sources: Vec<(PathBuf, bool)> = vec![
        (database, true),
        (config.join("metrics.sqlite"), true),
        (metadata.join("catalogue.db"), true),
        (metadata.join("prefetch.db"), true),
        (metadata.join("repair-revisions.db"), true),
        (config.join("regrab/library-link-removals.jsonl"), false),
        (config.join("prefetch-predictions.json"), false),
        (config.join("plex-library-metadata.json"), false),
        (config.join("imported-content-sweep.json"), false),
    ];
    for path in extras {
        if !path.is_absolute() || stamp(path)?.is_none() {
            return Err(Error::Invalid(
                "explicit journal must be an existing absolute file",
            ));
        }
        sources.push((path.clone(), sqlite(path)?));
    }
    if destination.to_str().is_none() || sources.iter().any(|(p, _)| p.to_str().is_none()) {
        return Err(Error::Invalid("legacy export paths must be UTF-8"));
    }
    sources.sort();
    sources.dedup();
    let watched: Vec<_> = sources
        .iter()
        .flat_map(|(p, sqlite)| {
            let mut paths = vec![p.clone()];
            if *sqlite {
                paths.push(PathBuf::from(format!("{}-wal", p.display())));
            }
            paths
        })
        .map(|p| Ok((p.clone(), witness(&p)?)))
        .collect::<Result<_, Error>>()?;
    // The effective snapshot and source witnesses must belong to the same generation.
    let cs_again = crate::Config::read(&config, vars).await?;
    if cs_again.archival_values() != cs.archival_values() {
        return Err(Error::Invalid("legacy settings changed before export"));
    }
    // Stored values are copied separately in db.sqlite. Environment overrides live
    // only in the package settings record and never become active Rust configuration.
    drop(cs_again);
    fs::DirBuilder::new().mode(0o700).create(destination)?;
    let mut incomplete = Incomplete(destination.to_path_buf());
    let mut manifest = Manifest {
        version: 1,
        id: Uuid::new_v4(),
        entries: Vec::new(),
        digest: String::new(),
    };
    for (source, sqlite) in &sources {
        let Some(info) = stamp(source)? else {
            continue;
        };
        let file = format!(
            "{:03}.{}",
            manifest.entries.len(),
            if *sqlite { "sqlite" } else { "journal" }
        );
        let dest = destination.join(&file);
        if *sqlite {
            if info.bytes > MAX_DATABASE_BYTES {
                return Err(Error::Invalid("legacy database exceeds export bound"));
            }
            let mut connection = SqliteConnectOptions::new()
                .filename(source)
                .read_only(true)
                .connect()
                .await?;
            let pages: i64 = sqlx::query_scalar("PRAGMA page_count")
                .fetch_one(&mut connection)
                .await?;
            let size: i64 = sqlx::query_scalar("PRAGMA page_size")
                .fetch_one(&mut connection)
                .await?;
            if pages < 0
                || size <= 0
                || u64::try_from(pages)
                    .unwrap_or(u64::MAX)
                    .saturating_mul(u64::try_from(size).unwrap_or(u64::MAX))
                    > MAX_DATABASE_BYTES
            {
                return Err(Error::Invalid(
                    "legacy logical database exceeds export bound",
                ));
            }
            // VACUUM INTO takes a consistent SQLite snapshot including committed WAL.
            sqlx::query("VACUUM INTO $1")
                .bind(dest.to_string_lossy().as_ref())
                .execute(&mut connection)
                .await?;
            connection.close().await?;
            fs::set_permissions(&dest, fs::Permissions::from_mode(0o600))?;
            input(&dest)?.sync_all()?;
        } else {
            let mut from = input(source)?.take(MAX_FILE_BYTES + 1);
            let mut to = output(&dest)?;
            if std::io::copy(&mut from, &mut to)? > MAX_FILE_BYTES {
                return Err(Error::Invalid("legacy journal exceeds export bound"));
            }
            to.sync_all()?;
        }
        let (bytes, sha256) = hash(
            &dest,
            if *sqlite {
                MAX_DATABASE_BYTES
            } else {
                MAX_FILE_BYTES
            },
        )?;
        manifest.entries.push(Entry {
            file,
            source: source.to_string_lossy().into_owned(),
            kind: if *sqlite {
                "sqlite_snapshot"
            } else {
                "journal"
            }
            .into(),
            bytes,
            sha256,
        });
    }
    let values = serde_json::to_vec(
        &serde_json::json!({"settings":cs.archival_values(),"fallback_environment":cs.archival_fallbacks()}),
    )?;
    if values.len() as u64 > MAX_FILE_BYTES {
        return Err(Error::Invalid("legacy settings exceed export bound"));
    }
    let name = format!("{:03}.settings", manifest.entries.len());
    let path = destination.join(&name);
    let mut file = output(&path)?;
    file.write_all(&values)?;
    file.sync_all()?;
    let (bytes, sha256) = hash(&path, MAX_FILE_BYTES)?;
    manifest.entries.push(Entry {
        file: name,
        source: "effective legacy settings (environment overlaid)".into(),
        kind: "settings".into(),
        bytes,
        sha256,
    });
    for (path, before) in watched {
        if witness(&path)? != before {
            return Err(Error::Invalid(
                "legacy state changed during export; stop 1.x and retry",
            ));
        }
    }
    manifest.digest = digest(&manifest)?;
    let raw = serde_json::to_vec_pretty(&manifest)?;
    if raw.len() as u64 > MAX_MANIFEST_BYTES {
        return Err(Error::Invalid("legacy manifest exceeds export bound"));
    }
    let mut file = output(&destination.join("manifest.json"))?;
    file.write_all(&raw)?;
    file.sync_all()?;
    File::open(destination)?.sync_all()?;
    incomplete.0 = PathBuf::new();
    Ok(manifest)
}

/// Verifies package-local bytes only. No source paths are followed or jobs replayed.
pub fn verify(directory: &Path) -> Result<Manifest, Error> {
    let mut raw = Vec::new();
    input(&directory.join("manifest.json"))?
        .take(MAX_MANIFEST_BYTES + 1)
        .read_to_end(&mut raw)?;
    if raw.len() as u64 > MAX_MANIFEST_BYTES {
        return Err(Error::Invalid("legacy manifest exceeds export bound"));
    }
    let manifest: Manifest = serde_json::from_slice(&raw)?;
    if manifest.version != 1
        || manifest.entries.len() > MAX_FILES
        || manifest.digest != digest(&manifest)?
    {
        return Err(Error::Invalid("legacy manifest is invalid"));
    }
    for (i, e) in manifest.entries.iter().enumerate() {
        let ext = match e.kind.as_str() {
            "sqlite_snapshot" => "sqlite",
            "journal" => "journal",
            "settings" => "settings",
            _ => return Err(Error::Invalid("unknown legacy evidence kind")),
        };
        if e.file != format!("{i:03}.{ext}") {
            return Err(Error::Invalid("legacy evidence path is invalid"));
        }
        let (bytes, sha) = hash(
            &directory.join(&e.file),
            if ext == "sqlite" {
                MAX_DATABASE_BYTES
            } else {
                MAX_FILE_BYTES
            },
        )?;
        if bytes != e.bytes || sha != e.sha256 {
            return Err(Error::Invalid("legacy evidence checksum differs"));
        }
    }
    Ok(manifest)
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Generated package assertions.
mod tests {
    use super::*;
    use std::os::unix::fs::symlink;
    async fn fixture() -> (PathBuf, sqlx::SqliteConnection) {
        let root = std::env::temp_dir().join(format!("infinidysk-fork-export-{}", Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let mut db = SqliteConnectOptions::new()
            .filename(root.join("db.sqlite"))
            .create_if_missing(true)
            .journal_mode(sqlx::sqlite::SqliteJournalMode::Wal)
            .connect()
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE ConfigItems(ConfigName TEXT PRIMARY KEY,ConfigValue TEXT);\nCREATE TABLE Accounts(Type INTEGER,Username TEXT,PasswordHash TEXT,RandomSalt TEXT);\nINSERT INTO ConfigItems VALUES('fork.unknown','private-fixture-value');\nCREATE TABLE ForkJournal(Id TEXT,Stage TEXT);INSERT INTO ForkJournal VALUES('fixture-intent','delete-uncertain');").execute(&mut db).await.unwrap();
        fs::create_dir(root.join("regrab")).unwrap();
        fs::write(
            root.join("regrab/library-link-removals.jsonl"),
            b"{\"stage\":\"intent\"}\n",
        )
        .unwrap();
        (root, db)
    }
    #[tokio::test]
    async fn preserves_unknown_settings_wal_rows_and_operational_journals_without_activation() {
        let (root, mut db) = fixture().await;
        let extra = root.join("runner.jsonl");
        fs::write(&extra, b"{\"state\":\"uncertain\"}\n").unwrap();
        let package = root.join("package");
        let manifest = export(
            &root,
            &package,
            &[extra],
            vec![
                (
                    "NZBDAV_CONFIG__FORK__UNKNOWN".into(),
                    "environment-fixture".into(),
                ),
                (
                    "FRONTEND_BACKEND_API_KEY".into(),
                    "dummy-private-key".into(),
                ),
            ],
        )
        .await
        .unwrap();
        assert_eq!(verify(&package).unwrap().digest, manifest.digest);
        assert_eq!(fs::metadata(&package).unwrap().mode() & 0o777, 0o700);
        for e in &manifest.entries {
            assert_eq!(
                fs::metadata(package.join(&e.file)).unwrap().mode() & 0o777,
                0o600
            );
        }
        let settings = manifest
            .entries
            .iter()
            .find(|e| e.kind == "settings")
            .unwrap();
        let values: serde_json::Value =
            serde_json::from_slice(&fs::read(package.join(&settings.file)).unwrap()).unwrap();
        assert_eq!(values["settings"]["fork.unknown"], "environment-fixture");
        assert_eq!(
            values["fallback_environment"]["FRONTEND_BACKEND_API_KEY"],
            "dummy-private-key"
        );
        let snapshot = manifest
            .entries
            .iter()
            .find(|e| e.source.ends_with("/db.sqlite"))
            .unwrap();
        let mut saved = SqliteConnectOptions::new()
            .filename(package.join(&snapshot.file))
            .read_only(true)
            .connect()
            .await
            .unwrap();
        let stage: String = sqlx::query_scalar("SELECT Stage FROM ForkJournal")
            .fetch_one(&mut saved)
            .await
            .unwrap();
        assert_eq!(stage, "delete-uncertain");
        let original: String = sqlx::query_scalar("SELECT ConfigValue FROM ConfigItems")
            .fetch_one(&mut db)
            .await
            .unwrap();
        assert_eq!(original, "private-fixture-value");
        assert!(
            manifest
                .entries
                .iter()
                .any(|e| e.source.ends_with("runner.jsonl"))
        );
        assert!(
            manifest
                .entries
                .iter()
                .any(|e| e.source.ends_with("library-link-removals.jsonl"))
        );
        saved.close().await.unwrap();
        db.close().await.unwrap();
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn refuses_existing_destination_symlinks_and_tampered_payloads() {
        let (root, db) = fixture().await;
        let package = root.join("package");
        for provider in ["postgres", "postgresql", "unknown"] {
            let rejected = root.join("rejected-provider");
            assert!(
                export(
                    &root,
                    &rejected,
                    &[],
                    vec![("DATABASE_PROVIDER".into(), provider.into())]
                )
                .await
                .is_err()
            );
            assert!(!rejected.exists());
        }
        export(&root, &package, &[], vec![]).await.unwrap();
        assert!(export(&root, &package, &[], vec![]).await.is_err());
        let m = verify(&package).unwrap();
        fs::write(package.join(&m.entries[0].file), b"changed").unwrap();
        assert!(verify(&package).is_err());
        let linked = root.join("linked.jsonl");
        symlink(root.join("regrab/library-link-removals.jsonl"), &linked).unwrap();
        assert!(
            export(&root, &root.join("rejected"), &[linked], vec![])
                .await
                .is_err()
        );
        assert!(!root.join("rejected").exists());
        fs::create_dir(root.join("native-cache-metadata")).unwrap();
        let bad = root.join("native-cache-metadata/prefetch.db");
        fs::write(&bad, b"invalid SQLite").unwrap();
        let incomplete = root.join("incomplete");
        assert!(export(&root, &incomplete, &[], vec![]).await.is_err());
        assert!(!incomplete.exists());
        assert_eq!(fs::read(&bad).unwrap(), b"invalid SQLite");
        db.close().await.unwrap();
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn source_stamps_detect_same_length_rewrites_and_package_paths_are_fixed() {
        let root = std::env::temp_dir().join(format!("infinidysk-fork-stamp-{}", Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let file = root.join("journal");
        fs::write(&file, b"one").unwrap();
        let before = witness(&file).unwrap();
        fs::write(&file, b"two").unwrap();
        assert_ne!(witness(&file).unwrap(), before);
        assert!(hash(&file, 2).is_err());
        let mut manifest = Manifest {
            version: 1,
            id: Uuid::new_v4(),
            entries: vec![Entry {
                file: "../outside".into(),
                source: "not-followed".into(),
                kind: "journal".into(),
                bytes: 0,
                sha256: String::new(),
            }],
            digest: String::new(),
        };
        manifest.digest = digest(&manifest).unwrap();
        fs::write(
            root.join("manifest.json"),
            serde_json::to_vec(&manifest).unwrap(),
        )
        .unwrap();
        assert!(verify(&root).is_err());
        fs::remove_dir_all(root).unwrap();
    }
}
