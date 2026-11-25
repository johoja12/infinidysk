//! The catalogue charges both reserved and valid blocks to their assigned folder.
use sqlx::{Executor, Sqlite, SqlitePool};

pub(super) async fn install(db: &SqlitePool) -> Result<(), sqlx::Error> {
    // An additive migration keeps old readers and writers compatible with this
    // catalogue. SQLite runs the same accounting triggers for both binaries.
    let mut tx = db.begin_with("BEGIN IMMEDIATE").await?;
    let installed: bool = sqlx::query_scalar(
        "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE name='folder_usage' AND type='table')",
    )
    .fetch_one(&mut *tx)
    .await?;
    if !installed {
        for statement in [
            "CREATE TABLE folder_usage(folder TEXT PRIMARY KEY, bytes INTEGER NOT NULL CHECK(bytes >= 0))",
            "INSERT INTO folder_usage SELECT e.folder,SUM(b.len) FROM blocks b JOIN entries e ON e.key=b.key GROUP BY e.folder",
            "CREATE TRIGGER track_block_insert AFTER INSERT ON blocks BEGIN
                INSERT INTO folder_usage SELECT folder,NEW.len FROM entries WHERE key=NEW.key
                ON CONFLICT(folder) DO UPDATE SET bytes=bytes+excluded.bytes;
             END",
            "CREATE TRIGGER track_block_length AFTER UPDATE OF len ON blocks WHEN OLD.len<>NEW.len BEGIN
                UPDATE folder_usage SET bytes=bytes-OLD.len+NEW.len
                    WHERE folder=(SELECT folder FROM entries WHERE key=NEW.key);
             END",
            "CREATE TRIGGER track_block_delete AFTER DELETE ON blocks BEGIN
                UPDATE folder_usage SET bytes=bytes-OLD.len
                    WHERE folder=(SELECT folder FROM entries WHERE key=OLD.key);
             END",
            // Delete child rows while the folder is still available to their
            // accounting trigger. The foreign-key cascade then has no work.
            "CREATE TRIGGER track_entry_delete BEFORE DELETE ON entries BEGIN
                DELETE FROM blocks WHERE key=OLD.key;
             END",
        ] {
            sqlx::query(statement).execute(&mut *tx).await?;
        }
    }
    tx.commit().await
}

pub(super) async fn used<'e>(
    db: impl Executor<'e, Database = Sqlite>,
    folder: &str,
) -> Result<u64, sqlx::Error> {
    let bytes: i64 =
        sqlx::query_scalar("SELECT COALESCE((SELECT bytes FROM folder_usage WHERE folder=$1),0)")
            .bind(folder)
            .fetch_one(db)
            .await?;
    Ok(bytes.cast_unsigned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::BLOCK;

    #[tokio::test]
    async fn existing_catalogues_charge_pending_blocks_and_account_for_eviction() {
        let db = SqlitePool::connect("sqlite::memory:").await.unwrap();
        for statement in [
            "CREATE TABLE entries(key TEXT PRIMARY KEY, folder TEXT NOT NULL)",
            "CREATE TABLE blocks(key TEXT NOT NULL REFERENCES entries(key) ON DELETE CASCADE,
                block INTEGER NOT NULL, len INTEGER NOT NULL, valid BOOLEAN NOT NULL,
                PRIMARY KEY(key,block))",
            "INSERT INTO entries VALUES ('existing','disk')",
            "INSERT INTO blocks VALUES ('existing',0,100,1),('existing',1,50,0)",
        ] {
            db.execute(statement).await.unwrap();
        }
        install(&db).await.unwrap();
        install(&db).await.unwrap();
        assert_eq!(used(&db, "disk").await.unwrap(), 150);

        let mut tx = db.begin().await.unwrap();
        sqlx::query("UPDATE blocks SET len=$1,valid=1 WHERE block=1")
            .bind(BLOCK.cast_signed())
            .execute(&mut *tx)
            .await
            .unwrap();
        assert_eq!(used(&mut *tx, "disk").await.unwrap(), BLOCK + 100);
        tx.rollback().await.unwrap();
        assert_eq!(used(&db, "disk").await.unwrap(), 150);

        db.execute("DELETE FROM entries WHERE key='existing'")
            .await
            .unwrap();
        assert_eq!(used(&db, "disk").await.unwrap(), 0);
        let rows: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM blocks")
            .fetch_one(&db)
            .await
            .unwrap();
        assert_eq!(rows, 0);
    }
}
