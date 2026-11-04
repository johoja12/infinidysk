//! Main-database witness of the authoritative local article repair ledger.
//! Accepted epochs only move forward, including late completion after cancellation.
use super::{Db, Error, Pool};
use infinidysk_core::{BlobId, ItemId};
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

impl Db {
    pub async fn health_repair_observed(
        &self,
        item: ItemId,
        revision: &[u8; 32],
    ) -> Result<bool, Error> {
        let sql =
            "SELECT EXISTS(SELECT 1 FROM repair_observations WHERE item_id=$1 AND revision=$2)";
        Ok(
            on!(self,pool=>sqlx::query_scalar(sql).bind(item.0).bind(revision.as_slice()).fetch_one(pool).await?),
        )
    }
    /// Record only a conclusive check of metadata and request generation still current.
    /// A crash before this receipt can cause an extra safe check, never a skipped one.
    pub async fn record_health_repair_observation(
        &self,
        item: ItemId,
        revision: &[u8; 32],
        blob: BlobId,
        raw: &[u8],
        generation: i64,
    ) -> Result<bool, Error> {
        let sql = "INSERT INTO repair_observations(item_id,revision) SELECT i.id,$2 FROM items i JOIN blobs b ON b.id=i.meta_blob WHERE i.id=$1 AND i.meta_blob=$3 AND b.data=$4 AND i.recheck_generation=$5 ON CONFLICT(item_id) DO UPDATE SET revision=excluded.revision";
        Ok(
            on!(self,pool=>sqlx::query(sql).bind(item.0).bind(revision.as_slice()).bind(blob.0).bind(raw).bind(generation).execute(pool).await?.rows_affected())
                == 1,
        )
    }
}

impl Db {
    /// Import immutable evidence before admitting any replacement mutation. A missing
    /// binding deliberately blocks all replacements until explicit reconciliation.
    pub async fn retain_legacy_repair_hold(
        &self,
        evidence_id: &str,
        item: Option<ItemId>,
        kind: &str,
        payload: &str,
    ) -> Result<(), Error> {
        self.retain_legacy_repair_request(evidence_id, item, kind, payload, None)
            .await
    }
    pub async fn retain_legacy_repair_request(
        &self,
        evidence_id: &str,
        item: Option<ItemId>,
        kind: &str,
        payload: &str,
        request: Option<&str>,
    ) -> Result<(), Error> {
        let sql = "INSERT INTO legacy_repair_holds(evidence_id,item_id,kind,payload,created_at,request_id) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(evidence_id) DO NOTHING";
        on!(self,pool=>sqlx::query(sql).bind(evidence_id).bind(item.map(|id| id.0)).bind(kind).bind(payload).bind(super::now_ms()).bind(request).execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Resolved evidence still protects its original ID: observing a replacement
    /// must never authorize a new repair of whatever now occupies that identity.
    /// Proven unbound records stop imposing a global hold.
    pub async fn legacy_repair_held(&self, item: ItemId) -> Result<bool, Error> {
        let sql = "SELECT EXISTS(SELECT 1 FROM legacy_repair_holds WHERE item_id=$1 OR (item_id IS NULL AND resolved_at IS NULL))";
        Ok(on!(self,pool=>sqlx::query_scalar(sql).bind(item.0).fetch_one(pool).await?))
    }
}

/// Private immutable legacy evidence; observation never authorizes an Arr write.
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct LegacyRepairHold {
    pub evidence_id: String,
    pub kind: String,
    pub payload: String,
    pub request_id: Option<String>,
}
impl Db {
    pub async fn pending_legacy_repairs(
        &self,
        before: i64,
        limit: u32,
    ) -> Result<Vec<LegacyRepairHold>, Error> {
        let sql = "SELECT evidence_id,kind,payload,request_id FROM legacy_repair_holds WHERE resolved_at IS NULL AND observed_at<=$1 ORDER BY observed_at,created_at,evidence_id LIMIT $2";
        Ok(
            on!(self,pool=>sqlx::query_as(sql).bind(before).bind(i64::from(limit.min(100))).fetch_all(pool).await?),
        )
    }
    /// Ambiguous histories stay held rather than selecting one by timestamp.
    pub async fn legacy_repair_requests(&self, request: &str) -> Result<Vec<String>, Error> {
        let sql = "SELECT payload FROM legacy_repair_holds WHERE kind='arr-request' AND request_id=$1 ORDER BY evidence_id LIMIT 2";
        Ok(on!(self,pool=>sqlx::query_scalar(sql).bind(request).fetch_all(pool).await?))
    }
    pub async fn observe_legacy_repair(
        &self,
        evidence: &str,
        proof: Option<&str>,
    ) -> Result<(), Error> {
        let sql = "UPDATE legacy_repair_holds SET observed_at=$3,proof=$2,resolved_at=CASE WHEN $2 IS NULL THEN NULL ELSE $3 END WHERE evidence_id=$1 AND resolved_at IS NULL";
        on!(self,pool=>sqlx::query(sql).bind(evidence).bind(proof).bind(super::now_ms()).execute(pool).await?.rows_affected());
        Ok(())
    }
}
