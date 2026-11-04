//! Source-bound PAR2 attempt journal. Publishing intent survives interruption;
//! only revision-checked transitions can acknowledge progress or a recovery result.
use super::{Db, Error, Pool};
use infinidysk_core::ItemId;
use uuid::Uuid;
const MAX_PENDING: i64 = 128;
const MAX_ROWS: i64 = 1024;
const MAX_PAYLOAD: usize = 1 << 20;
const STATES: &[&str] = &[
    "queued",
    "running",
    "publishing",
    "deferred",
    "healthy",
    "repaired",
    "unsupported",
    "limit",
    "superseded",
];
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct Attempt {
    pub id: Uuid,
    pub item_id: Uuid,
    pub source: Vec<u8>,
    pub state: String,
    pub priority: i16,
    pub next_attempt: i64,
    pub updated_at: i64,
    pub revision: i64,
    pub payload: String,
}
impl Attempt {
    fn validate(self) -> Result<Self, Error> {
        if self.id.is_nil()
            || self.item_id.is_nil()
            || self.source.len() != 32
            || self.revision < 0
            || !valid(
                &self.state,
                self.priority,
                self.next_attempt,
                self.updated_at,
                &self.payload,
            )
        {
            return Err(Error::Corrupt("invalid repair attempt"));
        }
        Ok(self)
    }
}
fn valid(state: &str, priority: i16, next: i64, now: i64, payload: &str) -> bool {
    STATES.contains(&state)
        && (0..=255).contains(&priority)
        && next >= 0
        && now >= 0
        && !payload.is_empty()
        && payload.len() <= MAX_PAYLOAD
}
impl Db {
    pub async fn repair_attempt(
        &self,
        item: ItemId,
        source: &[u8; 32],
    ) -> Result<Option<Attempt>, Error> {
        let sql = "SELECT id,item_id,source,state,priority,next_attempt,updated_at,revision,payload FROM par2_attempts WHERE item_id=$1 AND source=$2";
        let row: Option<Attempt> = on!(self,pool=>sqlx::query_as(sql).bind(item.0).bind(source.as_slice()).fetch_optional(pool).await?);
        row.map(Attempt::validate).transpose()
    }
    /// Idempotent admission. Capacity affects new work, never the readback of an
    /// accepted intent. The singleton row serializes count/eviction on both backends.
    pub async fn enqueue_repair_attempt(
        &self,
        item: ItemId,
        source: &[u8; 32],
        priority: i16,
        now: i64,
        payload: &str,
    ) -> Result<Option<Attempt>, Error> {
        if item.0.is_nil() || !valid("queued", priority, now, now, payload) {
            return Err(Error::Corrupt("invalid repair attempt admission"));
        }
        let id = Uuid::new_v4();
        let row: Option<Attempt> = on!(self,pool=>{
            let mut tx=pool.begin().await?;
            sqlx::query("UPDATE par2_attempt_state SET revision=revision+1 WHERE slot=1").execute(&mut *tx).await?;
            let old:Option<Attempt>=sqlx::query_as("SELECT id,item_id,source,state,priority,next_attempt,updated_at,revision,payload FROM par2_attempts WHERE item_id=$1 AND source=$2").bind(item.0).bind(source.as_slice()).fetch_optional(&mut *tx).await?;
            let row=if old.is_some() {old} else {
                let pending:i64=sqlx::query_scalar("SELECT COUNT(*) FROM par2_attempts WHERE state IN ('queued','running','publishing','deferred')").fetch_one(&mut *tx).await?;
                if pending>=MAX_PENDING {None} else {
                    let count:i64=sqlx::query_scalar("SELECT COUNT(*) FROM par2_attempts").fetch_one(&mut *tx).await?;
                    if count>=MAX_ROWS {
                        sqlx::query("DELETE FROM par2_attempts WHERE id IN (SELECT id FROM par2_attempts WHERE state IN ('healthy','repaired','unsupported','limit','superseded') ORDER BY updated_at,id LIMIT $1)").bind(count-MAX_ROWS+1).execute(&mut *tx).await?;
                    }
                    sqlx::query("INSERT INTO par2_attempts(id,item_id,source,state,priority,next_attempt,updated_at,revision,payload) VALUES($1,$2,$3,'queued',$4,$5,$5,0,$6)").bind(id).bind(item.0).bind(source.as_slice()).bind(priority).bind(now).bind(payload).execute(&mut *tx).await?;
                    sqlx::query_as("SELECT id,item_id,source,state,priority,next_attempt,updated_at,revision,payload FROM par2_attempts WHERE id=$1").bind(id).fetch_optional(&mut *tx).await?
                }
            };
            tx.commit().await?;
            row
        });
        row.map(Attempt::validate).transpose()
    }
    /// The original source and identity are immutable. A failed CAS requires readback,
    /// including when a previous database response was lost after accepting the write.
    pub async fn update_repair_attempt(
        &self,
        old: &Attempt,
        state: &str,
        priority: i16,
        next: i64,
        now: i64,
        payload: &str,
    ) -> Result<bool, Error> {
        if !valid(state, priority, next, now, payload) || old.revision == i64::MAX {
            return Err(Error::Corrupt("invalid repair attempt transition"));
        }
        let sql = "UPDATE par2_attempts SET state=$2,priority=$3,next_attempt=$4,updated_at=$5,payload=$6,revision=revision+1 WHERE id=$1 AND revision=$7 AND item_id=$8 AND source=$9";
        Ok(
            on!(self,pool=>sqlx::query(sql).bind(old.id).bind(state).bind(priority).bind(next).bind(now).bind(payload).bind(old.revision).bind(old.item_id).bind(&old.source).execute(pool).await?.rows_affected())
                == 1,
        )
    }
    pub async fn next_repair_attempt(&self, now: i64) -> Result<Option<Attempt>, Error> {
        let sql = "SELECT id,item_id,source,state,priority,next_attempt,updated_at,revision,payload FROM par2_attempts WHERE state IN ('queued','deferred','publishing') AND next_attempt<=$1 ORDER BY CASE WHEN state='publishing' THEN 0 ELSE 1 END,priority,next_attempt,id LIMIT 1";
        let row: Option<Attempt> =
            on!(self,pool=>sqlx::query_as(sql).bind(now).fetch_optional(pool).await?);
        row.map(Attempt::validate).transpose()
    }
    /// Called only after acquiring the authoritative ledger's exclusive writer.
    /// A running attempt has not entered publication: publishing is persisted first.
    pub async fn recover_running_repair_attempts(&self, now: i64) -> Result<u64, Error> {
        let sql = "UPDATE par2_attempts SET state='deferred',next_attempt=$1,updated_at=$1,revision=revision+1 WHERE state='running'";
        Ok(on!(self,pool=>sqlx::query(sql).bind(now).execute(pool).await?.rows_affected()))
    }
    pub async fn repair_attempts(&self, limit: u16) -> Result<Vec<Attempt>, Error> {
        let sql = "SELECT id,item_id,source,state,priority,next_attempt,updated_at,revision,payload FROM par2_attempts ORDER BY updated_at DESC,id DESC LIMIT $1";
        let rows: Vec<Attempt> = on!(self,pool=>sqlx::query_as(sql).bind(i64::from(limit).min(MAX_ROWS)).fetch_all(pool).await?);
        rows.into_iter().map(Attempt::validate).collect()
    }
}
