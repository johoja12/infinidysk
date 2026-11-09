//! Read-only Arr observation and guarded local retirement of replaced legacy sources.
use super::{Error, Health};
use infinidysk_db::repair::LegacyRepairHold;
use serde_json::Value;

fn url(value: &str) -> Option<String> {
    let parsed = reqwest::Url::parse(value).ok()?;
    if !matches!(parsed.scheme(), "http" | "https")
        || !parsed.username().is_empty()
        || parsed.password().is_some()
        || parsed.query().is_some()
        || parsed.fragment().is_some()
    {
        return None;
    }
    Some(parsed.as_str().trim_end_matches('/').to_owned())
}
fn arr_fingerprint(arr: &infinidysk_arr::Arr) -> Vec<u8> {
    use sha2::{Digest as _, Sha256};
    let mut hash = Sha256::new();
    for field in [
        arr.id.to_string(),
        arr.url.clone(),
        format!("{:?}", arr.profile),
        arr.api_key.clone(),
    ] {
        hash.update(field.len().to_be_bytes());
        hash.update(field.as_bytes());
    }
    hash.finalize().to_vec()
}
impl Health {
    pub async fn observe_legacy_repairs(&self, before: i64, limit: u32) -> Result<u32, Error> {
        let rows = self.db.pending_legacy_repairs(before, limit).await?;
        let mut resolved = 0;
        for row in rows {
            // Checkpoint failed/unknown observations too, so they cannot starve others.
            self.db
                .observe_legacy_repair(&row.evidence_id, None)
                .await?;
            let proof =
                tokio::time::timeout(std::time::Duration::from_secs(10), self.legacy_proof(&row))
                    .await;
            if let Ok(Ok(Some(proof))) = proof {
                self.db
                    .observe_legacy_repair(&row.evidence_id, Some(&proof))
                    .await?;
                resolved += 1;
            }
        }
        Ok(resolved)
    }
    async fn legacy_proof(&self, row: &LegacyRepairHold) -> Result<Option<String>, Error> {
        let evidence: Value = serde_json::from_str(&row.payload)?;
        let request = if row.kind == "arr-request" {
            evidence.clone()
        } else if row.kind == "link-removal" {
            let Some(id) = &row.request_id else {
                return Ok(None);
            };
            let requests = self.db.legacy_repair_requests(id).await?;
            if requests.len() != 1 {
                return Ok(None);
            }
            let request: Value = serde_json::from_str(&requests[0])?;
            if evidence["linkPath"].as_str().is_none()
                || evidence["linkPath"] != request["LibraryPath"]
                || evidence["davItemId"]
                    .as_str()
                    .is_some_and(|id| Some(id) != request["DavItemId"].as_str())
            {
                return Ok(None);
            }
            request
        } else {
            return Ok(None);
        };
        let Some(host) = request["ArrHost"].as_str().and_then(url) else {
            return Ok(None);
        };
        let instances: Vec<_> = self
            .arr
            .borrow()
            .iter()
            .filter(|arr| url(&arr.url).as_ref() == Some(&host))
            .cloned()
            .collect();
        if instances.len() != 1 {
            return Ok(None);
        }
        let arr = &instances[0];
        let Some(kind) = request["ArrMediaKind"].as_str() else {
            return Ok(None);
        };
        let Some(file) = request["ArrFileId"].as_i64() else {
            return Ok(None);
        };
        let Some(path) = request["LibraryPath"].as_str() else {
            return Ok(None);
        };
        let Some(ids) = request["ArrMediaIds"].as_str() else {
            return Ok(None);
        };
        if ids.len() > 4096 {
            return Ok(None);
        }
        let Ok(ids) = ids
            .split(',')
            .map(|id| id.trim().parse::<i64>())
            .collect::<Result<Vec<_>, _>>()
        else {
            return Ok(None);
        };
        if !infinidysk_arr::legacy_replacement_observed(&self.http, arr, kind, &ids, file, path)
            .await
            .unwrap_or(false)
        {
            return Ok(None);
        }
        if !self.arr.borrow().iter().any(|current| current == arr) {
            return Ok(None);
        }
        Ok(Some(serde_json::to_string(
            &serde_json::json!({"version":1,"arr_id":arr.id,"arr_url":host,"kind":kind,"media_ids":ids,"previous_file":file,"observed_at":infinidysk_db::now_ms(),"replacement_observed":true,"arr_fingerprint":arr_fingerprint(arr)}),
        )?))
    }
}

#[derive(serde::Deserialize)]
struct ImportedGeneration {
    version: u8,
    fingerprint: Vec<u8>,
    size: u64,
}
impl Health {
    /// No remote mutation is replayed. A fresh exact replacement proof permits only
    /// local retirement, behind reader, storage, configuration and generation guards.
    pub async fn retire_legacy_sources(&self, before: i64, limit: u32) -> Result<u32, Error> {
        let rows = self.db.legacy_retirement_candidates(before, limit).await?;
        let mut retired = 0;
        for row in rows {
            self.db.touch_legacy_retirement(&row.evidence_id).await?;
            match tokio::time::timeout(
                std::time::Duration::from_secs(10),
                self.retire_legacy_source(&row),
            )
            .await
            {
                Ok(Ok(true)) => retired += 1,
                Ok(Ok(false)) => {}
                Ok(Err(error)) => {
                    tracing::warn!(evidence=%row.evidence_id,reason=%error,"legacy source retirement deferred");
                }
                Err(_) => {
                    tracing::warn!(evidence=%row.evidence_id,reason="source checks timed out","legacy source retirement deferred");
                }
            }
        }
        Ok(retired)
    }
    async fn retire_legacy_source(&self, row: &LegacyRepairHold) -> Result<bool, Error> {
        use infinidysk_core::{FileMeta, ItemId};
        use infinidysk_nntp::RepairRevision;
        use sha2::{Digest as _, Sha256};
        let maintenance = self.db.write_permit().await;
        if (self.importing)()
            || !self.settings.borrow().enabled
            || !self.settings.borrow().auto_repair
        {
            return Ok(false);
        }
        let Some(id) = row.item_id.map(ItemId) else {
            return Ok(false);
        };
        // The predicate alone cannot prevent a new reader racing deletion.
        let Some(gate) = &self.read_gate else {
            return Ok(false);
        };
        if (self.in_use)(id) {
            return Ok(false);
        }
        let Some(item) = self.db.item(id).await? else {
            return Ok(false);
        };
        let Some(provenance) = self.db.import_source(id).await? else {
            return Ok(false);
        };
        let Ok(imported) = serde_json::from_str::<ImportedGeneration>(&provenance) else {
            return Ok(false);
        };
        let Some(meta_id) = item.meta_blob else {
            return Ok(false);
        };
        let Some(raw) = self.db.blob_limited(meta_id, 16_000_000).await? else {
            return Ok(false);
        };
        let meta = infinidysk_core::blob::decode_meta_limited(&raw, 64_000_000)?;
        if imported.version != 1
            || Some(imported.size) != item.size
            || imported.size != meta.size()
            || imported.fingerprint != Sha256::digest(meta.content_identity()?).as_slice()
        {
            return Ok(false);
        }
        let request: Value = serde_json::from_str(&row.payload)?;
        if request["DavItemId"].as_str() != Some(&id.0.to_string()) {
            return Ok(false);
        }
        let Some(path) = request["LibraryPath"].as_str() else {
            return Ok(false);
        };
        // Source and Arr proof are captured before blocking new readers.
        let Some(proof) = self.legacy_proof(row).await? else {
            return Ok(false);
        };
        let publication = std::sync::Arc::clone(&self.mutation_gate)
            .read_owned()
            .await;
        let Some(reader) = gate(id) else {
            return Ok(false);
        };
        if let Some(check) = &self.cached_copy
            && check(item.clone()).await != Some(false)
        {
            return Ok(false);
        }
        let runs = match &meta {
            FileMeta::Nzb(f) => vec![&f.segments],
            FileMeta::Multipart(f) => f.parts.iter().map(|p| &p.segments).collect(),
        };
        if runs.iter().any(|r| {
            r.items
                .iter()
                .any(|s| self.client.repair_revision(&s.id) != RepairRevision::Base)
        }) {
            return Ok(false);
        }
        if let Some(authority) = &self.repair_authority
            && !authority(id, super::RepairTarget::ArrPath(path.to_owned())).await?
        {
            return Ok(false);
        }
        let proof_value: Value = serde_json::from_str(&proof)?;
        if !self
            .arr
            .borrow()
            .iter()
            .any(|a| serde_json::json!(arr_fingerprint(a)) == proof_value["arr_fingerprint"])
        {
            return Ok(false);
        }
        let receipt=serde_json::json!({"version":1,"replacement":proof_value,"source_created_at":item.created_at,"metadata_sha256":Sha256::digest(&raw).to_vec(),"provenance_sha256":Sha256::digest(provenance.as_bytes()).to_vec()}).to_string();
        if !self.settings.borrow().enabled || !self.settings.borrow().auto_repair {
            return Ok(false);
        }
        let db = self.db.clone();
        let evidence = row.evidence_id.clone();
        // SQL COMMIT can finish after its caller is cancelled. The owned guards
        // stay with the mutation task until that transaction has actually settled.
        tokio::spawn(async move {
            let result = db
                .retire_legacy_source(&evidence, &item, &raw, &provenance, &receipt)
                .await;
            drop((maintenance, publication, reader));
            result
        })
        .await
        .map_err(|_| Error::Regrab("legacy retirement task failed"))?
        .map_err(Error::from)
    }
}
