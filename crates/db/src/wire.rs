//! Atomic forward-only daily wire credit; no stale read/modify/write counters.
use super::{Db, Error, Pool};
#[derive(Debug, sqlx::FromRow)]
pub struct Usage {
    pub day: i64,
    pub charged: i64,
    pub granted: i64,
}
impl Db {
    /// One autocommitted statement reserves before sockets read. A lost response
    /// may strand credit but cannot create credit on a retry. Clock rollback stops
    /// new reservations until it catches up to the durable high-water day.
    pub async fn reserve_warming_wire(
        &self,
        day: i64,
        requested: u64,
        limit: u64,
    ) -> Result<Option<Usage>, Error> {
        let requested = i64::try_from(requested)
            .map_err(|_| Error::Corrupt("wire reservation exceeds integer range"))?;
        let limit = i64::try_from(limit)
            .map_err(|_| Error::Corrupt("wire allowance exceeds integer range"))?;
        if day < 0 || requested <= 0 || limit <= 0 {
            return Err(Error::Corrupt("invalid wire reservation"));
        }
        let sql = "UPDATE prefetch_wire_budget SET day=$1,charged=(CASE WHEN day<$1 THEN 0 ELSE charged END)+(CASE WHEN $2<$3-(CASE WHEN day<$1 THEN 0 ELSE charged END) THEN $2 ELSE $3-(CASE WHEN day<$1 THEN 0 ELSE charged END) END),granted=CASE WHEN $2<$3-(CASE WHEN day<$1 THEN 0 ELSE charged END) THEN $2 ELSE $3-(CASE WHEN day<$1 THEN 0 ELSE charged END) END WHERE slot=1 AND day<=$1 AND (CASE WHEN day<$1 THEN 0 ELSE charged END)<$3 RETURNING day,charged,granted";
        Ok(
            on!(self,pool=>sqlx::query_as(sql).bind(day).bind(requested).bind(limit).fetch_optional(pool).await?),
        )
    }
    pub async fn warming_wire_usage(&self) -> Result<Usage, Error> {
        Ok(
            on!(self,pool=>sqlx::query_as("SELECT day,charged,granted FROM prefetch_wire_budget WHERE slot=1").fetch_one(pool).await?),
        )
    }
}

impl Db {
    /// Outcome history and the acknowledged queue state commit together. History
    /// is separate so block checkpoints do not rewrite all retained outcomes.
    pub async fn save_prefetch(
        &self,
        ledger: &str,
        events: &[(uuid::Uuid, i64, String)],
    ) -> Result<(), Error> {
        if ledger.len() > 16 * 1024 * 1024
            || events.len() > 1000
            || events
                .iter()
                .any(|(id, at, raw)| id.is_nil() || *at < 0 || raw.is_empty() || raw.len() > 65536)
        {
            return Err(Error::Corrupt("invalid warming history transaction"));
        }
        on!(self,pool=>{
            let mut tx=pool.begin().await?;
            sqlx::query("INSERT INTO config(key,value) VALUES('fork_prefetch_v1',$1) ON CONFLICT(key) DO UPDATE SET value=excluded.value").bind(ledger).execute(&mut *tx).await?;
            for (id,at,payload) in events {sqlx::query("INSERT INTO prefetch_history(id,recorded_at,payload) VALUES($1,$2,$3) ON CONFLICT(id) DO NOTHING").bind(id).bind(at).bind(payload).execute(&mut *tx).await?;}
            if !events.is_empty() {sqlx::query("DELETE FROM prefetch_history WHERE id IN (SELECT id FROM prefetch_history WHERE id NOT IN (SELECT id FROM prefetch_history ORDER BY recorded_at DESC,id DESC LIMIT 2000))").execute(&mut *tx).await?;}
            tx.commit().await?;
        });
        Ok(())
    }
    pub async fn prefetch_history(&self, limit: u16) -> Result<Vec<String>, Error> {
        Ok(
            on!(self,pool=>sqlx::query_scalar("SELECT payload FROM prefetch_history ORDER BY recorded_at DESC,id DESC LIMIT $1").bind(i64::from(limit.min(100))).fetch_all(pool).await?),
        )
    }
}

impl Db {
    /// Ensure the row exists before any ambiguous write. PostgreSQL can then use
    /// its row lock as a completion fence, including for the first queue save.
    pub async fn ensure_prefetch(&self, default: &str) -> Result<(), Error> {
        on!(self,pool=>sqlx::query("INSERT INTO config(key,value) VALUES('fork_prefetch_v1',$1) ON CONFLICT(key) DO NOTHING").bind(default).execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Fence unresolved writes before accepting readback. A plain WAL snapshot can
    /// observe the old state while a canceled worker's COMMIT is still completing.
    pub async fn read_prefetch(&self) -> Result<String, Error> {
        Ok(on!(self,pool=>{
            let mut tx=pool.begin().await?;
            sqlx::query("UPDATE config SET value=value WHERE key='fork_prefetch_v1'").execute(&mut *tx).await?;
            let raw=sqlx::query_scalar("SELECT value FROM config WHERE key='fork_prefetch_v1'").fetch_one(&mut *tx).await?;
            tx.commit().await?;raw
        }))
    }
}
