//! One resumable imported-content sample sweep. The candidate snapshot and state
//! change together; incomplete work never advances the durable cursor.
use super::{Db, Error, Pool};
use infinidysk_core::ItemId;
use uuid::Uuid;

#[derive(Clone, Debug, sqlx::FromRow)]
pub struct State {
    pub payload: String,
    pub revision: i64,
    pub total: i64,
}
impl Db {
    pub async fn content_sweep(&self) -> Result<State, Error> {
        let sql = "SELECT payload,revision,(SELECT COUNT(*) FROM content_sweep_items) AS total FROM content_sweep_state WHERE slot=1";
        Ok(on!(self,pool=>sqlx::query_as(sql).fetch_one(pool).await?))
    }
    pub async fn start_content_sweep(
        &self,
        revision: i64,
        payload: &str,
        category: Option<&str>,
    ) -> Result<bool, Error> {
        let prefix = category.map(|c| format!("/content/{c}/"));
        let changed = on!(self,pool=>{
            let mut tx=pool.begin().await?;
            let changed=sqlx::query("UPDATE content_sweep_state SET payload=$1,revision=revision+1 WHERE slot=1 AND revision=$2")
                .bind(payload).bind(revision).execute(&mut *tx).await?.rows_affected()==1;
            if changed {
                sqlx::query("DELETE FROM content_sweep_items").execute(&mut *tx).await?;
                sqlx::query("INSERT INTO content_sweep_items(item_id) SELECT id FROM items WHERE kind<>1 AND meta_blob IS NOT NULL AND path LIKE '/content/%' AND ($1 IS NULL OR SUBSTR(path,1,LENGTH($1))=$1)")
                    .bind(prefix.as_deref()).execute(&mut *tx).await?;
            }
            tx.commit().await?;changed
        });
        Ok(changed)
    }
    pub async fn checkpoint_content_sweep(
        &self,
        revision: i64,
        payload: &str,
    ) -> Result<bool, Error> {
        let sql = "UPDATE content_sweep_state SET payload=$1,revision=revision+1 WHERE slot=1 AND revision=$2";
        Ok(
            on!(self,pool=>sqlx::query(sql).bind(payload).bind(revision).execute(pool).await?.rows_affected())
                == 1,
        )
    }
    pub async fn next_sweep_item(&self, after: Option<ItemId>) -> Result<Option<ItemId>, Error> {
        let sql =
            "SELECT item_id FROM content_sweep_items WHERE item_id>$1 ORDER BY item_id LIMIT 1";
        let id: Option<Uuid> = on!(self,pool=>sqlx::query_scalar(sql).bind(after.map_or(Uuid::nil(),|id|id.0)).fetch_optional(pool).await?);
        Ok(id.map(ItemId))
    }
}
