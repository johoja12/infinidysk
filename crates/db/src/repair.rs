//! Main-database witness of the authoritative local article repair ledger.
//! Accepted epochs only move forward, including late completion after cancellation.
use super::{Db, Error, Pool};
use uuid::Uuid;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Lineage {
    pub id: Uuid,
    pub epoch: u64,
}
impl Db {
    pub async fn repair_lineage(&self) -> Result<Option<Lineage>, Error> {
        let saved: Option<(Uuid, i64)> = on!(self,pool=>sqlx::query_as("SELECT ledger_id,epoch FROM repair_lineage WHERE slot=1").fetch_optional(pool).await?);
        saved
            .map(|(id, epoch)| {
                if id.is_nil() || epoch < 0 {
                    return Err(Error::Corrupt("invalid repair lineage"));
                }
                Ok(Lineage {
                    id,
                    epoch: epoch.cast_unsigned(),
                })
            })
            .transpose()
    }
    /// False means a different ledger or an older epoch tried to claim this state.
    /// The statement is atomic on SQLite and PostgreSQL; no read/write gap exists.
    pub async fn bind_repair_lineage(&self, id: Uuid, epoch: u64) -> Result<bool, Error> {
        let Ok(epoch) = i64::try_from(epoch) else {
            return Ok(false);
        };
        if id.is_nil() {
            return Ok(false);
        }
        let sql = "INSERT INTO repair_lineage(slot,ledger_id,epoch) VALUES(1,$1,$2) ON CONFLICT(slot) DO UPDATE SET epoch=excluded.epoch WHERE repair_lineage.ledger_id=excluded.ledger_id AND repair_lineage.epoch<=excluded.epoch";
        Ok(
            on!(self,pool=>sqlx::query(sql).bind(id).bind(epoch).execute(pool).await?.rows_affected())
                == 1,
        )
    }
}
