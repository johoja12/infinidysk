//! Legacy uncertain actions are evidence, never fresh mutation authorization.
use std::io::{BufRead as _, BufReader};
use std::path::Path;

use futures_util::TryStreamExt as _;
use infinidysk_core::ItemId;
use infinidysk_db::Db;
use serde_json::Value;
use sha2::{Digest as _, Sha256};
use sqlx::AssertSqlSafe;

use crate::{Error, fork, open};

const MAX_RECORDS: usize = 100_000;
const MAX_LINE: usize = 1024 * 1024;

fn binding(value: &Value) -> Option<ItemId> {
    value
        .as_str()?
        .parse::<uuid::Uuid>()
        .ok()
        .filter(|id| !id.is_nil())
        .map(ItemId)
}
async fn retain(db: &Db, kind: &str, payload: &str, item: Option<ItemId>) -> Result<(), Error> {
    let id = fork::hex(&Sha256::digest(
        [kind.as_bytes(), b"\0", payload.as_bytes()].concat(),
    ));
    let row: Value = serde_json::from_str(payload)
        .map_err(|_| Error::Mapping("legacy repair evidence is invalid"))?;
    let request = binding(if kind == "arr-request" {
        &row["Id"]
    } else {
        &row["requestId"]
    })
    .map(|id| id.0.to_string());
    db.retain_legacy_repair_request(&id, item, kind, payload, request.as_deref())
        .await?;
    Ok(())
}

/// Reads only an offline source. Unknown fields are retained; unknown bindings
/// become global holds. Errors abort startup instead of silently dropping evidence.
pub async fn import(config: &Path, db: &Db) -> Result<(), Error> {
    let mut source = open(config).await?;
    let exists: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='ArrRegrabRequests')")
        .fetch_one(&mut source).await?;
    if exists {
        let columns: Vec<(i64, String, String, i64, Option<String>, i64)> =
            sqlx::query_as("PRAGMA table_info(ArrRegrabRequests)")
                .fetch_all(&mut source)
                .await?;
        if columns.len() > 128 || !columns.iter().any(|c| c.1 == "Status") {
            return Err(Error::Mapping("legacy repair schema requires review"));
        }
        // Identifiers come only from SQLite's schema and are quoted; no SQL values
        // or user fragments are interpolated. Retain future fields in the evidence.
        let fields = columns
            .iter()
            .map(|c| {
                format!(
                    "'{}',\"{}\"",
                    c.1.replace('\'', "''"),
                    c.1.replace('"', "\"\"")
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        let query = format!("SELECT json_object({fields}) FROM ArrRegrabRequests LIMIT 100001");
        let mut rows = sqlx::query_scalar::<_, String>(AssertSqlSafe(query)).fetch(&mut source);
        let mut count = 0usize;
        let mut bytes = 0usize;
        while let Some(payload) = rows.try_next().await? {
            count += 1;
            bytes = bytes.saturating_add(payload.len());
            if count > MAX_RECORDS || payload.len() > MAX_LINE || bytes > 256 * 1024 * 1024 {
                return Err(Error::Mapping("legacy repair evidence exceeds bound"));
            }
            let row: Value = serde_json::from_str(&payload)
                .map_err(|_| Error::Mapping("legacy repair evidence is invalid"))?;
            // Even failed/skipped records can follow an accepted, uncertain request.
            // Only an explicitly observed terminal replacement avoids a hold.
            if row["Status"].as_str() != Some("replaced") {
                retain(db, "arr-request", &payload, binding(&row["DavItemId"])).await?;
            }
        }
    }
    let path = config.join("regrab/library-link-removals.jsonl");
    if !path
        .try_exists()
        .map_err(|_| Error::Mapping("legacy removal journal is inaccessible"))?
    {
        return Ok(());
    }
    let records = tokio::task::spawn_blocking(move || journal(&path)).await??;
    for payload in records {
        let row: Value = serde_json::from_str(&payload)
            .map_err(|_| Error::Mapping("legacy removal journal is invalid"))?;
        retain(db, "link-removal", &payload, binding(&row["davItemId"])).await?;
    }
    Ok(())
}
fn journal(path: &Path) -> Result<Vec<String>, Error> {
    let file = fork::input(path).map_err(|_| Error::Mapping("legacy removal journal is unsafe"))?;
    let mut reader = BufReader::new(file);
    let mut records = Vec::new();
    let mut bytes = 0usize;
    for n in 0..=MAX_RECORDS {
        let mut line = Vec::new();
        let read = std::io::Read::take(&mut reader, (MAX_LINE + 1) as u64)
            .read_until(b'\n', &mut line)
            .map_err(|_| Error::Mapping("legacy removal journal cannot be read"))?;
        if read == 0 {
            return Ok(records);
        }
        bytes = bytes.saturating_add(read);
        if n == MAX_RECORDS
            || read > MAX_LINE
            || bytes > 256 * 1024 * 1024
            || !line.ends_with(b"\n")
        {
            return Err(Error::Mapping(
                "legacy removal journal exceeds bound or is incomplete",
            ));
        }
        let payload = String::from_utf8(line)
            .map_err(|_| Error::Mapping("legacy removal journal is invalid"))?;
        let _: Value = serde_json::from_str(&payload)
            .map_err(|_| Error::Mapping("legacy removal journal is invalid"))?;
        records.push(payload);
    }
    unreachable!("bounded loop returns on the final record")
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Generated offline source and restart assertions.
mod tests {
    use super::*;
    use sqlx::{ConnectOptions as _, Connection as _, sqlite::SqliteConnectOptions};
    use uuid::Uuid;

    #[tokio::test]
    async fn requests_and_removal_intents_survive_restart_without_replay() {
        let root = std::env::temp_dir().join(format!("id-legacy-holds-{}", Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        let mut source = SqliteConnectOptions::new()
            .filename(root.join("db.sqlite"))
            .create_if_missing(true)
            .connect()
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE ArrRegrabRequests(Id TEXT,Status TEXT,DavItemId TEXT,Attempts INTEGER,FutureField TEXT)").execute(&mut source).await.unwrap();
        let pending = ItemId::new();
        let terminal = ItemId::new();
        let failed = ItemId::new();
        for (status, item) in [
            ("requested", pending),
            ("replaced", terminal),
            ("failed", failed),
        ] {
            sqlx::query("INSERT INTO ArrRegrabRequests VALUES($1,$2,$3,1,'retained')")
                .bind(Uuid::new_v4().to_string())
                .bind(status)
                .bind(item.0.to_string())
                .execute(&mut source)
                .await
                .unwrap();
        }
        source.close().await.unwrap();
        let url = format!("sqlite:{}", root.join("target.sqlite").display());
        let db = Db::connect(&url).await.unwrap();
        import(&root, &db).await.unwrap();
        assert!(db.legacy_repair_held(pending).await.unwrap());
        assert!(db.legacy_repair_held(failed).await.unwrap());
        assert!(!db.legacy_repair_held(terminal).await.unwrap());
        assert!(db.regrab_intent(pending).await.unwrap().is_none());
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        import(&root, &db).await.unwrap();
        assert!(db.legacy_repair_held(pending).await.unwrap());
        std::fs::create_dir(root.join("regrab")).unwrap();
        std::fs::write(
            root.join("regrab/library-link-removals.jsonl"),
            b"{\"event\":\"intent\",\"davItemId\":null}\n",
        )
        .unwrap();
        import(&root, &db).await.unwrap();
        assert!(
            db.legacy_repair_held(terminal).await.unwrap(),
            "unbound intent fences all replacements"
        );
        db.close().await;
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn torn_journal_is_not_silently_skipped() {
        let file = std::env::temp_dir().join(format!("id-torn-holds-{}", Uuid::new_v4()));
        std::fs::write(&file, b"{\"event\":\"intent\"}").unwrap();
        assert!(journal(&file).is_err());
        std::fs::remove_file(file).unwrap();
    }
}
