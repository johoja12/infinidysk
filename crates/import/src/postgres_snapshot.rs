//! Read-only C# PostgreSQL snapshots for the existing SQLite-based importer.
//! Textual source rows and PostgreSQL types are retained beside the translated
//! adapter. This is migration evidence, not a PostgreSQL disaster-recovery backup.
use std::fs::{self, File};
use std::io::{Read as _, Write as _};
use std::os::unix::fs::DirBuilderExt as _;
use std::path::Path;

use futures_util::TryStreamExt as _;
use jiff::tz::TimeZone;
use serde::{Deserialize, Serialize};
use sqlx::postgres::PgConnectOptions;
use sqlx::sqlite::SqliteConnectOptions;
use sqlx::{ConnectOptions as _, Connection as _, Row as _};
use uuid::Uuid;

use crate::fork::{Error, Incomplete, hash, hex, input, output};
use sha2::{Digest as _, Sha256};

const MAX_BYTES: u64 = 4 * 1024 * 1024 * 1024;
const MAX_CELL: usize = 16 * 1024 * 1024;
const MARKER: &str = "postgres-source.json";
const PENDING: &str = "postgres-snapshot.pending";
const FILES: [&str; 3] = ["db.sqlite", "postgres-schema.json", "postgres-rows.jsonl"];

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Receipt {
    pub version: u8,
    pub id: Uuid,
    pub timezone: String,
    #[serde(default)]
    pub quarantined_cells: u64,
    pub files: Vec<crate::fork::Entry>,
    pub digest: String,
}
#[derive(Debug, Serialize, Deserialize)]
struct Table {
    name: String,
    columns: Vec<Column>,
}
#[derive(Debug, Serialize, Deserialize)]
struct Column {
    name: String,
    postgres_type: String,
}
/// Invalid UTF-8 in optional metadata is retained as bytes, never normalized.
#[derive(Serialize)]
#[serde(untagged)]
enum RawCell {
    Text(String),
    Invalid { invalid_utf8_hex: String },
}
fn cell(bytes: Vec<u8>, table: &str, column: &Column) -> Result<RawCell, Error> {
    match String::from_utf8(bytes) {
        Ok(value) => Ok(RawCell::Text(value)),
        Err(error) if table == "LibraryLinkMaps" && column.name == "TargetText" => {
            Ok(RawCell::Invalid {
                invalid_utf8_hex: hex(error.as_bytes()),
            })
        }
        Err(_) => Err(Error::Invalid(
            "invalid UTF-8 in active legacy data; snapshot refused",
        )),
    }
}
fn ident(value: &str) -> String {
    format!("\"{}\"", value.replace('"', "\"\""))
}
fn receipt_digest(receipt: &Receipt) -> Result<String, Error> {
    let bytes = if receipt.version == 1 {
        serde_json::to_vec(&(
            receipt.version,
            receipt.id,
            &receipt.timezone,
            &receipt.files,
        ))?
    } else {
        serde_json::to_vec(&(
            receipt.version,
            receipt.id,
            &receipt.timezone,
            &receipt.files,
            receipt.quarantined_cells,
        ))?
    };
    Ok(hex(&Sha256::digest(bytes)))
}

/// Writes a private, bounded snapshot of all public base tables in one read-only
/// repeatable-read transaction. The stopped source's blobs/journals must be copied
/// separately into this new configuration directory before running the importer.
/// `connection` is supplied through stdin by the CLI, never printed or stored.
pub async fn snapshot(
    connection: &str,
    destination: &Path,
    timezone: &str,
) -> Result<Receipt, Error> {
    snapshot_inner(connection, destination, timezone)
        .await
        .map_err(|error| match error {
            Error::Database(_) => Error::Invalid(
                "legacy PostgreSQL snapshot query or conversion failed; no snapshot was accepted",
            ),
            other => other,
        })
}
async fn snapshot_inner(
    connection: &str,
    destination: &Path,
    timezone: &str,
) -> Result<Receipt, Error> {
    TimeZone::get(timezone)
        .map_err(|_| Error::Invalid("source timezone must be an IANA timezone"))?;
    let options: PgConnectOptions = connection
        .trim()
        .parse()
        .map_err(|_| Error::Invalid("invalid PostgreSQL connection settings"))?;
    // Deliberately erase driver errors at this credential-bearing boundary.
    let mut source = options
        .disable_statement_logging()
        .connect()
        .await
        .map_err(|_| Error::Invalid("could not connect to the legacy PostgreSQL source"))?;
    let encoding: String = sqlx::query_scalar("SHOW server_encoding")
        .fetch_one(&mut source)
        .await?;
    if !matches!(encoding.as_str(), "UTF8" | "SQL_ASCII") {
        return Err(Error::Invalid(
            "legacy database encoding requires explicit translation review",
        ));
    }
    let mut transaction = source.begin().await?;
    sqlx::raw_sql("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY; SET LOCAL statement_timeout='60s'; SET LOCAL client_encoding='SQL_ASCII'; SET LOCAL DateStyle='ISO, YMD'; SET LOCAL TIME ZONE 'UTC'; SET LOCAL bytea_output='hex'; SET LOCAL extra_float_digits=3")
        .execute(&mut *transaction).await?;
    let foreign: i64 = sqlx::query_scalar("SELECT count(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('public','information_schema') AND table_schema NOT LIKE 'pg_%'")
        .fetch_one(&mut *transaction).await?;
    if foreign != 0 {
        return Err(Error::Invalid(
            "legacy PostgreSQL has non-public tables; preserve them with a native backup before migration",
        ));
    }
    let names: Vec<String> = sqlx::query_scalar("SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name")
        .fetch_all(&mut *transaction).await?;
    if names.is_empty() || names.len() > 512 {
        return Err(Error::Invalid(
            "legacy PostgreSQL table count is outside the snapshot bound",
        ));
    }
    fs::DirBuilder::new().mode(0o700).create(destination)?;
    let mut incomplete = Incomplete(destination.to_path_buf());
    output(&destination.join(PENDING))?.sync_all()?;
    File::open(destination)?.sync_all()?;
    let sqlite_path = destination.join(FILES[0]);
    output(&sqlite_path)?.sync_all()?;
    let mut adapter = SqliteConnectOptions::new()
        .filename(&sqlite_path)
        .connect()
        .await?;
    let mut write = adapter.begin().await?;
    let mut raw = output(&destination.join(FILES[2]))?;
    let mut raw_bytes = 0_u64;
    let mut tables = Vec::new();
    let mut quarantined_cells = 0_u64;
    for name in names {
        let columns: Vec<(String,String)> = sqlx::query_as("SELECT a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod) FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname=$1 AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum")
            .bind(&name).fetch_all(&mut *transaction).await?;
        if columns.is_empty() || columns.len() > 1600 {
            return Err(Error::Invalid(
                "legacy PostgreSQL column count is outside the snapshot bound",
            ));
        }
        let table = Table {
            name,
            columns: columns
                .into_iter()
                .map(|(name, postgres_type)| Column {
                    name,
                    postgres_type,
                })
                .collect(),
        };
        let definitions = table
            .columns
            .iter()
            .map(|c| format!("{} {}", ident(&c.name), affinity(&c.postgres_type)))
            .collect::<Vec<_>>()
            .join(",");
        sqlx::query(sqlx::AssertSqlSafe(format!(
            "CREATE TABLE {} ({definitions})",
            ident(&table.name)
        )))
        .execute(&mut *write)
        .await?;
        // A projected row is bounded on the server before crossing the protocol.
        // Oversize rows fail the snapshot rather than being silently truncated.
        let row_size = table
            .columns
            .iter()
            .map(|c| format!("coalesce(octet_length({}::text)::bigint,0)", ident(&c.name)))
            .collect::<Vec<_>>()
            .join("+");
        let oversize: bool = sqlx::query_scalar(sqlx::AssertSqlSafe(format!(
            "SELECT EXISTS(SELECT 1 FROM public.{} WHERE {row_size}>{MAX_CELL})",
            ident(&table.name)
        )))
        .fetch_one(&mut *transaction)
        .await?;
        if oversize {
            return Err(Error::Invalid("legacy PostgreSQL row exceeds byte bound"));
        }
        let expressions = table
            .columns
            .iter()
            .map(|c| format!("pg_catalog.textsend({}::text)", ident(&c.name)))
            .collect::<Vec<_>>()
            .join(",");
        let select = format!("SELECT {expressions} FROM public.{}", ident(&table.name));
        let insert = format!(
            "INSERT INTO {} VALUES ({})",
            ident(&table.name),
            (1..=table.columns.len())
                .map(|i| format!("${i}"))
                .collect::<Vec<_>>()
                .join(",")
        );
        let mut rows = sqlx::query(sqlx::AssertSqlSafe(select)).fetch(&mut *transaction);
        while let Some(row) = rows.try_next().await? {
            let values = (0..table.columns.len())
                .map(|i| row.try_get::<Option<Vec<u8>>, _>(i))
                .collect::<Result<Vec<_>, _>>()?;
            let values = values
                .into_iter()
                .zip(&table.columns)
                .map(|(value, column)| {
                    value
                        .map(|bytes| cell(bytes, &table.name, column))
                        .transpose()
                })
                .collect::<Result<Vec<_>, _>>()?;
            quarantined_cells += values
                .iter()
                .filter(|value| matches!(value, Some(RawCell::Invalid { .. })))
                .count() as u64;
            let encoded = serde_json::to_vec(&(&table.name, &values))?;
            raw_bytes = raw_bytes.saturating_add(encoded.len() as u64 + 1);
            if raw_bytes > MAX_BYTES {
                return Err(Error::Invalid(
                    "legacy PostgreSQL snapshot exceeds byte bound",
                ));
            }
            raw.write_all(&encoded)?;
            raw.write_all(b"\n")?;
            let mut query = sqlx::query(sqlx::AssertSqlSafe(insert.clone()));
            for (value, column) in values.into_iter().zip(&table.columns) {
                let value = match value {
                    Some(RawCell::Invalid { invalid_utf8_hex }) => {
                        // Dynamic SQLite BLOB storage prevents accidental String activation.
                        query = query.bind(binary(&format!("\\x{invalid_utf8_hex}"))?);
                        continue;
                    }
                    Some(RawCell::Text(value)) => Some(value),
                    None => None,
                };
                query = match column.postgres_type.as_str() {
                    "boolean" => query.bind(value.map(|v| i64::from(v == "true"))),
                    "smallint" | "integer" | "bigint" => query.bind(
                        value
                            .map(|v| v.parse::<i64>())
                            .transpose()
                            .map_err(|_| Error::Invalid("legacy integer is invalid"))?,
                    ),
                    "bytea" => query.bind(value.map(|v| binary(&v)).transpose()?),
                    kind if kind.starts_with("timestamp") => {
                        if kind.ends_with("with time zone") {
                            return Err(Error::Invalid(
                                "legacy timestamp-with-timezone column requires explicit translation review",
                            ));
                        }
                        // Npgsql maps DateTime.MinValue/MaxValue to infinities.
                        // Keep original infinities in raw evidence; adapt only to the
                        // millisecond precision that the importer consumes.
                        query.bind(value.map(|v| match v.as_str() {
                            "infinity" => "9999-12-31 23:59:59.999".to_owned(),
                            "-infinity" => "0001-01-01 00:00:00".to_owned(),
                            _ => v,
                        }))
                    }
                    _ => query.bind(value),
                };
            }
            query.execute(&mut *write).await?;
        }
        tables.push(table);
    }
    write.commit().await?;
    adapter.close().await?;
    raw.sync_all()?;
    let mut schema = output(&destination.join(FILES[1]))?;
    schema.write_all(&serde_json::to_vec_pretty(&tables)?)?;
    schema.sync_all()?;
    // Source transaction never writes. Commit only releases its consistent snapshot.
    transaction.commit().await?;
    let mut receipt = Receipt {
        version: 2,
        id: Uuid::new_v4(),
        timezone: timezone.to_owned(),
        quarantined_cells,
        files: Vec::new(),
        digest: String::new(),
    };
    for file in FILES {
        let (bytes, sha256) = hash(&destination.join(file), MAX_BYTES)?;
        input(&destination.join(file))?.sync_all()?;
        receipt.files.push(crate::fork::Entry {
            file: file.into(),
            source: "legacy PostgreSQL snapshot".into(),
            kind: "postgres_snapshot".into(),
            bytes,
            sha256,
        });
    }
    receipt.digest = receipt_digest(&receipt)?;
    let mut marker = output(&destination.join(MARKER))?;
    marker.write_all(&serde_json::to_vec_pretty(&receipt)?)?;
    marker.sync_all()?;
    File::open(destination)?.sync_all()?;
    fs::remove_file(destination.join(PENDING))?;
    File::open(destination)?.sync_all()?;
    incomplete.0.clear();
    Ok(receipt)
}
fn affinity(kind: &str) -> &'static str {
    match kind {
        "boolean" | "smallint" | "integer" | "bigint" => "INTEGER",
        "bytea" => "BLOB",
        _ => "TEXT",
    }
}
fn binary(value: &str) -> Result<Vec<u8>, Error> {
    let hex = value
        .strip_prefix("\\x")
        .ok_or(Error::Invalid("legacy binary encoding is invalid"))?;
    if !hex.len().is_multiple_of(2) {
        return Err(Error::Invalid("legacy binary encoding is invalid"));
    }
    hex.as_bytes()
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| {
            let digit = |b: u8| match b {
                b'0'..=b'9' => Some(b - b'0'),
                b'a'..=b'f' => Some(b - b'a' + 10),
                _ => None,
            };
            digit(pair[0])
                .zip(digit(pair[1]))
                .map(|(a, b)| a * 16 + b)
                .ok_or(Error::Invalid("legacy binary encoding is invalid"))
        })
        .collect()
}
/// Verifies only fixed package-local paths. The caller must compare the returned
/// digest with its independently retained export receipt before accepting a package.
pub fn verify(directory: &Path) -> Result<Receipt, Error> {
    if directory.join(PENDING).try_exists()? {
        return Err(Error::Invalid("legacy PostgreSQL snapshot is incomplete"));
    }
    let mut raw = Vec::new();
    input(&directory.join(MARKER))?
        .take(128 * 1024 + 1)
        .read_to_end(&mut raw)?;
    if raw.len() > 128 * 1024 {
        return Err(Error::Invalid("legacy PostgreSQL receipt exceeds bound"));
    }
    let receipt: Receipt = serde_json::from_slice(&raw)?;
    if !matches!(receipt.version, 1 | 2)
        || (receipt.version == 1 && receipt.quarantined_cells != 0)
        || receipt.files.len() != FILES.len()
        || receipt.digest != receipt_digest(&receipt)?
        || TimeZone::get(&receipt.timezone).is_err()
    {
        return Err(Error::Invalid("legacy PostgreSQL receipt is invalid"));
    }
    for (entry, name) in receipt.files.iter().zip(FILES) {
        if entry.file != name || entry.kind != "postgres_snapshot" {
            return Err(Error::Invalid("legacy PostgreSQL snapshot path is invalid"));
        }
        let (bytes, sha) = hash(&directory.join(name), MAX_BYTES)?;
        if bytes != entry.bytes || sha != entry.sha256 {
            return Err(Error::Invalid(
                "legacy PostgreSQL snapshot checksum differs",
            ));
        }
    }
    Ok(receipt)
}
pub(crate) fn timezone(config: &Path) -> Result<TimeZone, Error> {
    if config.join(PENDING).try_exists()? {
        return Err(Error::Invalid("legacy PostgreSQL snapshot is incomplete"));
    }
    if !config.join(MARKER).try_exists()? {
        return Ok(TimeZone::system());
    }
    let receipt = verify(config)?;
    TimeZone::get(&receipt.timezone)
        .map_err(|_| Error::Invalid("legacy source timezone is invalid"))
}

/// Validation can hash large snapshots; keep this work off the async executor.
pub(crate) async fn source_timezone(config: &Path) -> Result<TimeZone, crate::Error> {
    let config = config.to_path_buf();
    tokio::task::spawn_blocking(move || timezone(&config))
        .await?
        .map_err(|error| crate::Error::Source(Box::new(error)))
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Generated private snapshot assertions.
mod tests {
    use super::*;

    #[test]
    fn identifiers_and_binary_values_are_not_sql_or_paths() {
        assert_eq!(ident("quoted\"table"), "\"quoted\"\"table\"");
        assert_eq!(binary("\\x00ff12").unwrap(), [0, 255, 18]);
        for value in ["00ff", "\\x0", "\\xzz"] {
            assert!(binary(value).is_err());
        }
    }
    #[test]
    fn invalid_active_text_is_never_normalized_or_accepted() {
        let column = Column {
            name: "Path".into(),
            postgres_type: "text".into(),
        };
        assert!(cell(vec![0xff], "DavItems", &column).is_err());
        let column = Column {
            name: "TargetText".into(),
            postgres_type: "text".into(),
        };
        assert!(
            matches!(cell(vec![0xff], "LibraryLinkMaps", &column).unwrap(),RawCell::Invalid{invalid_utf8_hex} if invalid_utf8_hex=="ff")
        );
    }

    #[tokio::test]
    async fn snapshots_postgres_types_unknown_state_and_source_timezone_without_writes() {
        let Ok(url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            return;
        };
        let options: PgConnectOptions = url.parse().unwrap();
        let mut admin = options.clone().connect().await.unwrap();
        let name = format!("legacy_snapshot_{}", Uuid::new_v4().simple());
        sqlx::query(sqlx::AssertSqlSafe(format!(
            "CREATE DATABASE {} TEMPLATE template0 ENCODING 'SQL_ASCII' LC_COLLATE 'C' LC_CTYPE 'C'",
            ident(&name)
        )))
        .execute(&mut admin)
        .await
        .unwrap();
        let options = options.database(&name);
        let mut source = options.clone().connect().await.unwrap();
        sqlx::raw_sql("CREATE TABLE \"ConfigItems\"(\"ConfigName\" text,\"ConfigValue\" text);INSERT INTO \"ConfigItems\" VALUES('fork.unknown','private-fixture');CREATE TABLE \"Accounts\"(\"Type\" integer,\"Username\" text,\"PasswordHash\" text,\"RandomSalt\" text);CREATE TABLE \"ForkJournal\"(\"Id\" uuid,\"Pending\" boolean,\"Bytes\" bigint,\"Payload\" bytea,\"Created\" timestamp without time zone,\"Nullable\" text,\"Unknown\" jsonb);INSERT INTO \"ForkJournal\" VALUES('00000000-0000-0000-0000-000000000123',true,9223372036854775807,decode('00ff12','hex'),'2026-07-04 14:23:45.123',NULL,'{\"stage\":\"delete-uncertain\"}');")
            .execute(&mut source).await.unwrap();
        sqlx::raw_sql(r#"CREATE TABLE "DavItems"("Id" uuid,"ParentId" uuid,"Name" text,"Path" text,"CreatedAt" timestamp(3) without time zone,"Type" integer,"SubType" integer,"FileSize" bigint,"FileBlobId" uuid,"NzbBlobId" uuid,"ReleaseDate" bigint,"LastHealthCheck" bigint,"NextHealthCheck" bigint,"HealthRepairPending" boolean,"GeneratedStrmPath" text,"GeneratedSymlinkPath" text,"ArrDownloadId" text);
CREATE TABLE "HistoryItems"("Id" uuid,"CreatedAt" timestamp without time zone,"FileName" text,"JobName" text,"Category" text,"DownloadStatus" integer,"TotalSegmentBytes" bigint,"DownloadTimeSeconds" bigint,"FailMessage" text,"DownloadDirId" uuid,"NzbBlobId" uuid);
CREATE TABLE "QueueItems"("Id" uuid,"CreatedAt" timestamp without time zone,"FileName" text,"JobName" text,"Category" text,"Priority" integer,"PostProcessing" integer,"TotalSegmentBytes" bigint,"PauseUntil" timestamp without time zone,"SortOrder" bigint);
CREATE TABLE "QueueNzbContents"("Id" uuid,"NzbContents" text);
CREATE TABLE "__EFMigrationsHistory"("MigrationId" text);INSERT INTO "__EFMigrationsHistory" VALUES('20261001000000');
INSERT INTO "DavItems"("Id","ParentId","Name","Path","CreatedAt","Type","SubType") VALUES('00000000-0000-0000-0000-000000000456','00000000-0000-0000-0000-000000000002','fixture','/content/fixture','2026-07-04 14:23:45.123',1,101);
CREATE TABLE "Sentinels"("Created" timestamp without time zone);INSERT INTO "Sentinels" VALUES('infinity'),('-infinity');"#).execute(&mut source).await.unwrap();
        let root = std::env::temp_dir().join(format!("infinidysk-pg-snapshot-{}", Uuid::new_v4()));
        // Connection URI is private input only, never included in receipts/errors.
        let connection = options.to_url_lossy().to_string();
        sqlx::raw_sql(r#"CREATE TABLE "LibraryLinkMaps"("Id" uuid,"TargetText" text);
SET client_encoding='SQL_ASCII';
INSERT INTO "LibraryLinkMaps" VALUES('00000000-0000-0000-0000-000000000124',convert_from(decode('ee9345','hex'),'SQL_ASCII'));
SET client_encoding='UTF8';"#).execute(&mut source).await.unwrap();
        let receipt = snapshot(&connection, &root, "America/New_York")
            .await
            .unwrap();
        assert_eq!(verify(&root).unwrap().digest, receipt.digest);
        assert_eq!(
            timezone(&root).unwrap(),
            TimeZone::get("America/New_York").unwrap()
        );
        let cs = crate::Config::read(&root, []).await.unwrap();
        assert_eq!(cs.get("fork.unknown"), Some("private-fixture"));
        let mut adapter = crate::open(&root).await.unwrap();
        assert_eq!(receipt.version, 2);
        assert_eq!(receipt.quarantined_cells, 1);
        let held: Vec<u8> = sqlx::query_scalar("SELECT TargetText FROM LibraryLinkMaps")
            .fetch_one(&mut adapter)
            .await
            .unwrap();
        assert_eq!(held, [0xee, 0x93, 0x45]);
        assert!(
            fs::read_to_string(root.join("postgres-rows.jsonl"))
                .unwrap()
                .contains("invalid_utf8_hex")
        );
        let row = sqlx::query("SELECT * FROM ForkJournal")
            .fetch_one(&mut adapter)
            .await
            .unwrap();
        assert_eq!(
            row.get::<String, _>("Id"),
            "00000000-0000-0000-0000-000000000123"
        );
        assert!(row.get::<bool, _>("Pending"));
        assert_eq!(row.get::<i64, _>("Bytes"), i64::MAX);
        assert_eq!(row.get::<Vec<u8>, _>("Payload"), [0, 255, 18]);
        assert_eq!(row.get::<String, _>("Created"), "2026-07-04 14:23:45.123");
        assert_eq!(row.get::<Option<String>, _>("Nullable"), None);
        let wall = "2026-07-04T14:23:45.123"
            .parse::<jiff::civil::DateTime>()
            .unwrap();
        assert_eq!(
            wall.to_zoned(timezone(&root).unwrap())
                .unwrap()
                .timestamp()
                .as_millisecond(),
            "2026-07-04T18:23:45.123Z"
                .parse::<jiff::Timestamp>()
                .unwrap()
                .as_millisecond()
        );
        let sentinels: Vec<String> =
            sqlx::query_scalar("SELECT Created FROM Sentinels ORDER BY Created")
                .fetch_all(&mut adapter)
                .await
                .unwrap();
        assert_eq!(
            sentinels,
            ["0001-01-01 00:00:00", "9999-12-31 23:59:59.999"]
        );
        let db = infinidysk_db::Db::connect("sqlite::memory:").await.unwrap();
        let client = infinidysk_nntp::Client::new(vec![]);
        assert!(crate::survey(&root).await.unwrap().supported());
        let done = crate::apply(&root, &db, &client, "/fixture-completed")
            .await
            .unwrap();
        assert_eq!(done.directories, 1);
        let item = db
            .item(infinidysk_core::ItemId(Uuid::from_u128(0x456)))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            item.created_at,
            "2026-07-04T18:23:45.123Z"
                .parse::<jiff::Timestamp>()
                .unwrap()
                .as_millisecond()
        );
        db.close().await;
        let evidence = root.join("fork-evidence");
        let bundle = crate::fork::export(
            &root,
            &evidence,
            &[],
            vec![("DATABASE_PROVIDER".into(), "postgres".into())],
        )
        .await
        .unwrap();
        assert_eq!(
            crate::fork::verify(&evidence).unwrap().digest,
            bundle.digest
        );
        assert_eq!(
            bundle
                .entries
                .iter()
                .filter(|e| e.kind == "postgres_snapshot")
                .count(),
            3
        );
        output(&root.join(PENDING)).unwrap().sync_all().unwrap();
        assert!(verify(&root).is_err());
        assert!(crate::open(&root).await.is_err());
        fs::remove_file(root.join(PENDING)).unwrap();
        adapter.close().await.unwrap();
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"ForkJournal\"")
                .fetch_one(&mut source)
                .await
                .unwrap(),
            1
        );
        let raw = fs::read_to_string(root.join(FILES[2])).unwrap();
        assert!(raw.contains("delete-uncertain"));
        assert!(
            !fs::read_to_string(root.join(MARKER))
                .unwrap()
                .contains(&connection)
        );
        assert!(snapshot(&connection, &root, "UTC").await.is_err());
        let changed = root.with_extension("rejected");
        sqlx::raw_sql("ALTER TABLE \"ForkJournal\" ADD COLUMN \"Unsupported\" timestamp with time zone;INSERT INTO \"ForkJournal\"(\"Unsupported\") VALUES(now())").execute(&mut source).await.unwrap();
        assert!(snapshot(&connection, &changed, "UTC").await.is_err());
        assert!(!changed.exists());
        fs::write(root.join(FILES[2]), b"tampered").unwrap();
        assert!(verify(&root).is_err());
        assert!(crate::open(&root).await.is_err());
        source.close().await.unwrap();
        sqlx::query(sqlx::AssertSqlSafe(format!(
            "DROP DATABASE {}",
            ident(&name)
        )))
        .execute(&mut admin)
        .await
        .unwrap();
        fs::remove_dir_all(root).unwrap();
    }
}
