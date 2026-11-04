//! Read-only observation of imported uncertainty. No mutation-ready state escapes.
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
            &serde_json::json!({"version":1,"arr_id":arr.id,"arr_url":host,"kind":kind,"media_ids":ids,"previous_file":file,"observed_at":infinidysk_db::now_ms(),"replacement_observed":true}),
        )?))
    }
}
