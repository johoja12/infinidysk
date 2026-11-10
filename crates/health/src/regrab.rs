//! The shared durable coordinator. HTTP clients never own database state, and a
//! successful checkpoint is required before every external mutation.
use super::{Arr, Error, Health};
use infinidysk_arr::{self as arr, Owner, Regrab, RegrabStage};
use infinidysk_core::blob;
use infinidysk_db::{Db, Item, RegrabIntent};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fmt::Write as _,
    sync::{
        Arc,
        atomic::{AtomicI64, Ordering},
    },
};
#[derive(Serialize, Deserialize)]
pub(super) struct Saved {
    pub arr_id: String,
    pub job_name: String,
    pub progress: Regrab,
    #[serde(default)]
    pub arr_scope: Option<String>,
    #[serde(default)]
    pub abandoned: bool,
    #[serde(default)]
    pub reviewed_at: i64,
    #[serde(default)]
    pub observed_at: Option<i64>,
    #[serde(default)]
    pub observation: Option<String>,
}
fn arr_scope(arr: &Arr) -> String {
    let input = format!(
        "{}\0{}\0{:?}",
        arr.id,
        arr.url.trim_end_matches('/'),
        arr.profile
    );
    Sha256::digest(input)
        .iter()
        .fold(String::with_capacity(64), |mut s, b| {
            write!(s, "{b:02x}").expect("formatting into String cannot fail");
            s
        })
}
pub(super) async fn scope(db: &Db, item: &Item, arr: &Arr) -> Result<String, Error> {
    let id = item
        .meta_blob
        .ok_or(Error::Regrab("repair source metadata is missing"))?;
    let raw = db
        .blob(id)
        .await?
        .ok_or(Error::Regrab("repair source metadata is missing"))?;
    let identity = blob::decode_meta(&raw)?.content_identity()?;
    let input = serde_json::to_vec(&(
        item.id.0,
        item.size,
        &identity,
        arr.id,
        arr.url.trim_end_matches('/'),
        format!("{:?}", arr.profile),
    ))?;
    Ok(Sha256::digest(input)
        .iter()
        .fold(String::with_capacity(64), |mut s, b| {
            write!(s, "{b:02x}").expect("string formatting");
            s
        }))
}
/// Resume keeps the last durable mutation checkpoint. It never resets an uncertain
/// operation to Prepared or dispatches a replacement command by itself.
#[derive(Clone, Copy, Debug)]
pub enum Control {
    Observe,
    Abandon,
    Resume,
}
impl Health {
    pub async fn run_regrab_observer(&self) {
        let mut tick = tokio::time::interval(std::time::Duration::from_secs(15));
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            tokio::select! {_=tick.tick()=>{},()=self.legacy_wake.notified()=>{}}
            if let Err(error) = self
                .observe_legacy_repairs(infinidysk_db::now_ms() - 5 * super::MINUTE_MS, 3)
                .await
            {
                tracing::warn!(reason=%error,"legacy repair observation deferred");
            }
            if let Err(error) = self
                .retire_legacy_sources(infinidysk_db::now_ms() - 5 * super::MINUTE_MS, 3)
                .await
            {
                tracing::warn!(reason=%error,"legacy source retirement deferred");
            }
            if let Err(error) = self
                .observe_regrabs(infinidysk_db::now_ms() - 5 * super::MINUTE_MS, 3)
                .await
            {
                tracing::warn!(reason=%error,"regrab observation deferred");
            }
        }
    }
    /// Observation is independent of source lifetime and performs no external writes.
    pub async fn observe_regrabs(&self, before: i64, limit: u32) -> Result<u32, Error> {
        let rows = self.db.regrab_intents(before, false, limit).await?;
        let mut observed = 0;
        for (id, row) in rows {
            let Ok(mut saved) = serde_json::from_str::<Saved>(&row.payload) else {
                self.db
                    .checkpoint_regrab(id, &row.scope, row.revision, &row.payload, false)
                    .await?;
                tracing::warn!(item=%id,reason="journal unreadable; original retained","regrab observation deferred");
                continue;
            };
            if saved.abandoned || saved.progress.version != 1 {
                self.db
                    .checkpoint_regrab(id, &row.scope, row.revision, &row.payload, row.terminal)
                    .await?;
                continue;
            }
            let result = self.observe_saved(id, &row, &mut saved).await;
            saved.observed_at = Some(infinidysk_db::now_ms());
            saved.observation = Some(
                match result {
                    Ok(true) => {
                        saved.progress.stage = RegrabStage::Replaced;
                        "replacement observed"
                    }
                    Ok(false) => "replacement not yet observed",
                    Err(_) => "source or Arr could not be verified; observation will retry",
                }
                .into(),
            );
            observed += u32::from(
                self.db
                    .checkpoint_regrab(
                        id,
                        &row.scope,
                        row.revision,
                        &serde_json::to_string(&saved)?,
                        saved.progress.terminal(),
                    )
                    .await?,
            );
        }
        Ok(observed)
    }
    async fn observe_saved(
        &self,
        id: infinidysk_core::ItemId,
        row: &RegrabIntent,
        saved: &mut Saved,
    ) -> Result<bool, Error> {
        let configured = self
            .arr
            .borrow()
            .iter()
            .find(|a| a.id.to_string() == saved.arr_id)
            .cloned()
            .ok_or(Error::Regrab("Arr is no longer configured"))?;
        let expected = arr_scope(&configured);
        if let Some(item) = self.db.item(id).await? {
            if scope(&self.db, &item, &configured).await? != row.scope {
                return Err(Error::Regrab("source identity changed"));
            }
        } else if saved.arr_scope.as_deref() != Some(expected.as_str()) {
            return Err(Error::Regrab(
                "removed source lacks verified Arr configuration provenance",
            ));
        }
        if saved.arr_scope.as_ref().is_some_and(|s| s != &expected) {
            return Err(Error::Regrab("Arr identity changed"));
        }
        saved.arr_scope = Some(expected);
        tokio::time::timeout(
            std::time::Duration::from_secs(10),
            arr::observe_tracked(&self.http, &configured, &mut saved.progress),
        )
        .await
        .map_err(|_| Error::Regrab("Arr observation timed out"))?
        .map_err(|_| Error::Regrab("Arr observation unavailable"))?;
        Ok(saved.progress.terminal())
    }
    pub async fn control_regrab(
        &self,
        id: infinidysk_core::ItemId,
        revision: i64,
        control: Control,
    ) -> Result<bool, Error> {
        let Some(row) = self.db.regrab_intent(id).await? else {
            return Ok(false);
        };
        if row.revision != revision {
            return Ok(false);
        }
        let mut saved: Saved = serde_json::from_str(&row.payload)?;
        if saved.progress.version != 1 {
            return Err(Error::Regrab("unsupported regrab journal version"));
        }
        if matches!(control, Control::Resume) {
            let item = self.db.item(id).await?.ok_or(Error::Regrab(
                "removed sources support observation or abandon only",
            ))?;
            let configured = self
                .arr
                .borrow()
                .iter()
                .find(|a| a.id.to_string() == saved.arr_id)
                .cloned()
                .ok_or(Error::Regrab("Arr is no longer configured"))?;
            if scope(&self.db, &item, &configured).await? != row.scope || saved.progress.terminal()
            {
                return Err(Error::Regrab("source or Arr identity changed"));
            }
            saved.abandoned = false;
            saved.reviewed_at = infinidysk_db::now_ms();
        } else if matches!(control, Control::Abandon) {
            if saved.progress.terminal() {
                return Err(Error::Regrab("completed replacement cannot be abandoned"));
            }
            saved.abandoned = true;
            saved.observation =
                Some("abandoned by administrator; external actions are not undone".into());
        } else {
            let replaced = self.observe_saved(id, &row, &mut saved).await?;
            saved.observed_at = Some(infinidysk_db::now_ms());
            if replaced {
                saved.progress.stage = RegrabStage::Replaced;
            }
            saved.observation = Some(
                if replaced {
                    "replacement observed"
                } else {
                    "replacement not yet observed"
                }
                .into(),
            );
        }
        if !self
            .db
            .checkpoint_regrab(
                id,
                &row.scope,
                revision,
                &serde_json::to_string(&saved)?,
                saved.abandoned || saved.progress.terminal(),
            )
            .await?
        {
            return Ok(false);
        }
        if matches!(control, Control::Resume) {
            self.request_repair(id).await?;
        }
        Ok(true)
    }
    pub(super) async fn tracked_regrab(
        &self,
        item: &Item,
        arr: &Arr,
        owner: &Owner,
        job_name: &str,
        existing: Option<RegrabIntent>,
    ) -> Result<Result<(), arr::Error>, Error> {
        let writing = self.db.write_permit().await;
        if self.db.legacy_repair_held(item.id).await? {
            return Ok(Err(arr::Error::Unexpected(
                "legacy repair evidence requires reconciliation",
            )));
        }
        let expected = scope(&self.db, item, arr).await?;
        let initial = Saved {
            arr_id: arr.id.to_string(),
            job_name: job_name.to_owned(),
            progress: Regrab::new(owner),
            arr_scope: Some(arr_scope(arr)),
            abandoned: false,
            reviewed_at: 0,
            observed_at: None,
            observation: None,
        };
        let row = match existing {
            Some(row) => row,
            None => {
                self.db
                    .prepare_regrab(item.id, &expected, &serde_json::to_string(&initial)?)
                    .await?
            }
        };
        if row.scope != expected {
            return Ok(Err(arr::Error::Unexpected(
                "regrab identity changed; review the outstanding intent",
            )));
        }
        let mut saved: Saved = serde_json::from_str(&row.payload)?;
        if saved.abandoned {
            return Ok(Err(arr::Error::Unexpected(
                "regrab was abandoned; resume it explicitly",
            )));
        }
        if infinidysk_db::now_ms().saturating_sub(row.created_at.max(saved.reviewed_at))
            > super::DAY_MS
            && matches!(
                saved.progress.stage,
                RegrabStage::BlocklistPending
                    | RegrabStage::DeletePending
                    | RegrabStage::SearchPending
            )
        {
            return Ok(Err(arr::Error::Unexpected(
                "Arr mutation remains uncertain after a day; manual review is required",
            )));
        }
        let version = Arc::new(AtomicI64::new(row.revision));
        let db = self.db.clone();
        let id = item.id;
        let arr_id = saved.arr_id.clone();
        let name = saved.job_name.clone();
        let bound_arr = arr_scope(arr);
        let reviewed_at = saved.reviewed_at;
        let configuration = self.arr.clone();
        let readers = &self.in_use;
        let authority = &self.repair_authority;
        let checkpoint = move |progress: Regrab| {
            let (db, scope, version, arr_id, job_name) = (
                db.clone(),
                expected.clone(),
                Arc::clone(&version),
                arr_id.clone(),
                name.clone(),
            );
            let bound_arr = bound_arr.clone();
            let configuration = configuration.clone();
            async move {
                if matches!(
                    progress.stage,
                    RegrabStage::BlocklistPending
                        | RegrabStage::DeletePending
                        | RegrabStage::SearchPending
                ) && readers(id)
                {
                    return Err(arr::Error::Uncertain("reader opened; regrab waits"));
                }
                if matches!(
                    progress.stage,
                    RegrabStage::BlocklistPending
                        | RegrabStage::DeletePending
                        | RegrabStage::SearchPending
                ) && let Some(authority) = authority
                    && !authority(
                        id,
                        super::RepairTarget::ArrPath(progress.owner.file_path().to_owned()),
                    )
                    .await
                    .map_err(|_| arr::Error::Checkpoint)?
                {
                    return Err(arr::Error::Unexpected(
                        "read-only scan folder cannot authorize regrab",
                    ));
                }
                let _writing = db.write_permit().await;
                let configured = configuration
                    .borrow()
                    .iter()
                    .find(|a| a.id.to_string() == arr_id)
                    .cloned()
                    .ok_or(arr::Error::Checkpoint)?;
                let current = db
                    .item(id)
                    .await
                    .map_err(|_| arr::Error::Checkpoint)?
                    .ok_or(arr::Error::Checkpoint)?;
                if super::regrab::scope(&db, &current, &configured)
                    .await
                    .map_err(|_| arr::Error::Checkpoint)?
                    != scope
                {
                    return Err(arr::Error::Checkpoint);
                }
                let terminal = progress.terminal();
                let payload = serde_json::to_string(&Saved {
                    arr_id,
                    job_name,
                    progress,
                    arr_scope: Some(bound_arr),
                    abandoned: false,
                    reviewed_at,
                    observed_at: None,
                    observation: None,
                })
                .map_err(|_| arr::Error::Checkpoint)?;
                let revision = version.load(Ordering::Acquire);
                if !db
                    .checkpoint_regrab(id, &scope, revision, &payload, terminal)
                    .await
                    .map_err(|_| arr::Error::Checkpoint)?
                {
                    return Err(arr::Error::Checkpoint);
                }
                version.store(revision + 1, Ordering::Release);
                Ok(())
            }
        };
        drop(writing);
        let result = tokio::time::timeout(
            std::time::Duration::from_secs(25),
            arr::replace_tracked(
                &self.http,
                arr,
                &mut saved.progress,
                self.arr_wait,
                checkpoint,
            ),
        )
        .await
        .unwrap_or(Err(arr::Error::Uncertain(
            "repair pass timed out; journal observation will resume",
        )));
        if result.is_err()
            && matches!(
                saved.progress.stage,
                RegrabStage::BlocklistPending
                    | RegrabStage::DeletePending
                    | RegrabStage::SearchPending
            )
        {
            return Ok(Err(arr::Error::Uncertain(
                "Arr mutation acknowledgement is uncertain; inspecting before retry",
            )));
        }
        Ok(result)
    }
    /// Saved identity remains available even after the Arr no longer lists the old file.
    pub(super) async fn observe_regrab(&self, item: &Item) -> Result<bool, Error> {
        let Some(row) = self.db.regrab_intent(item.id).await? else {
            return Ok(false);
        };
        let mut saved: Saved = serde_json::from_str(&row.payload)?;
        let arr = self
            .arr
            .borrow()
            .iter()
            .find(|a| a.id.to_string() == saved.arr_id)
            .cloned();
        let Some(arr) = arr else {
            return Ok(false);
        };
        if scope(&self.db, item, &arr).await? != row.scope {
            return Ok(false);
        }
        if !tokio::time::timeout(
            std::time::Duration::from_secs(10),
            arr::regrab_replaced(&self.http, &arr, &saved.progress),
        )
        .await
        .ok()
        .and_then(Result::ok)
        .unwrap_or(false)
        {
            return Ok(false);
        }
        saved.progress.stage = RegrabStage::Replaced;
        Ok(self
            .db
            .checkpoint_regrab(
                item.id,
                &row.scope,
                row.revision,
                &serde_json::to_string(&saved)?,
                true,
            )
            .await?)
    }
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
mod tests {
    use super::*;
    use axum::{
        Json, Router,
        extract::{Request, State},
        http::StatusCode,
        response::{IntoResponse, Response},
    };
    use infinidysk_core::{BlobId, FileMeta, ItemKind, NzbFile, Segment, Segments};
    use infinidysk_db::{HealthState, Recheck};
    use std::sync::atomic::{AtomicUsize, Ordering};
    use tokio::sync::watch;
    #[derive(Default)]
    struct Remote {
        deletes: AtomicUsize,
        searches: AtomicUsize,
        replaced: AtomicUsize,
    }
    async fn handle(State(s): State<Arc<Remote>>, req: Request) -> Response {
        let path = req.uri().path();
        let method = req.method().as_str();
        if method == "DELETE" {
            s.deletes.fetch_add(1, Ordering::SeqCst);
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
        if path.ends_with("/command") && method == "POST" {
            s.searches.fetch_add(1, Ordering::SeqCst);
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
        if path.ends_with("/moviefile/7") && s.deletes.load(Ordering::SeqCst) > 0 {
            return StatusCode::NOT_FOUND.into_response();
        }
        Json(match path {
            "/api/v3/rootfolder"=>serde_json::json!([{ "path":"/fixture" }]),
            "/api/v3/moviefile/7"=>serde_json::json!({"id":7,"movieId":3,"path":"/fixture/file.mkv"}),
            "/api/v3/movie/3"=>serde_json::json!({"movieFile":if s.replaced.load(Ordering::SeqCst)>0 {serde_json::json!({"id":8,"path":"/fixture/replacement.mkv"})}else if s.deletes.load(Ordering::SeqCst)>0 {serde_json::Value::Null}else{serde_json::json!({"id":7,"path":"/fixture/file.mkv"})}}),
            "/api/v3/history"=>serde_json::json!({"records":[],"totalRecords":0}),
            "/api/v3/queue/details"=>serde_json::json!([]),
            "/api/v3/command"=>if s.searches.load(Ordering::SeqCst)>0 {serde_json::json!([{"id":11,"body":{"name":"MoviesSearch","movieIds":[3]}}])}else{serde_json::json!([])},
            _=>panic!("unexpected fixture endpoint {method} {path}"),
        }).into_response()
    }
    #[tokio::test]
    async fn legacy_observation_proves_replacements_without_arr_mutation_or_repair_replay() {
        let path = std::env::temp_dir().join(format!(
            "id-legacy-observer-{}.sqlite",
            uuid::Uuid::new_v4()
        ));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .unwrap();
        let remote = Arc::new(Remote::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::new_v4(),
            name: "fixture".into(),
            profile: arr::Profile::Radarr,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "fixture-key".into(),
        };
        let router = Router::new()
            .fallback(handle)
            .with_state(Arc::clone(&remote));
        let task = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        let request_id = uuid::Uuid::new_v4().to_string();
        let item = infinidysk_core::ItemId::new();
        let request = serde_json::json!({"Id":request_id,"Status":"requested","ArrHost":arr.url,"ArrMediaKind":"movie","ArrFileId":7,"ArrMediaIds":"3","LibraryPath":"/fixture/file.mkv","DavItemId":item.0.to_string()}).to_string();
        db.retain_legacy_repair_request(
            "request",
            Some(item),
            "arr-request",
            &request,
            Some(&request_id),
        )
        .await
        .unwrap();
        let removal = serde_json::json!({"event":"intent","requestId":request_id,"linkPath":"/fixture/file.mkv"}).to_string();
        db.retain_legacy_repair_request(
            "removal",
            None,
            "link-removal",
            &removal,
            Some(&request_id),
        )
        .await
        .unwrap();
        let (_arr, arrs) = watch::channel(vec![arr]);
        let (_settings, settings) = watch::channel(super::super::Settings::default());
        let client = Arc::new(infinidysk_nntp::Client::new(
            Vec::<infinidysk_nntp::Provider>::new(),
        ));
        let health = Health::new(db.clone(), client, arrs, settings).unwrap();
        assert_eq!(
            health.observe_legacy_repairs(i64::MAX, 10).await.unwrap(),
            0
        );
        assert!(
            db.legacy_repair_held(infinidysk_core::ItemId::new())
                .await
                .unwrap()
        );
        remote.replaced.store(1, Ordering::SeqCst);
        assert_eq!(
            health.observe_legacy_repairs(i64::MAX, 10).await.unwrap(),
            2
        );
        assert!(
            db.pending_legacy_repairs(i64::MAX, 10)
                .await
                .unwrap()
                .is_empty()
        );
        assert!(
            !db.legacy_repair_held(infinidysk_core::ItemId::new())
                .await
                .unwrap()
        );
        assert!(
            db.legacy_repair_held(item).await.unwrap(),
            "original identity cannot start a fresh repair"
        );
        assert!(db.regrab_intent(item).await.unwrap().is_none());
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 0);
        assert_eq!(remote.searches.load(Ordering::SeqCst), 0);
        task.abort();
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
    #[tokio::test]
    async fn legacy_retirement_waits_for_readers_cache_and_authority_then_survives_restart() {
        let path = std::env::temp_dir().join(format!(
            "id-legacy-retirement-{}.sqlite",
            uuid::Uuid::new_v4()
        ));
        let url = format!("sqlite:{}", path.display());
        let db = Db::connect(&url).await.unwrap();
        let root = db
            .item(infinidysk_core::ItemId::CONTENT)
            .await
            .unwrap()
            .unwrap();
        let mut item = Item::child(&root, "fixture.mkv", ItemKind::NzbFile).unwrap();
        let blob = BlobId::new();
        item.meta_blob = Some(blob);
        item.size = Some(1);
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: "fixture@local".into(),
                    size: 1,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                ..Default::default()
            },
            proof: None,
        });
        let raw = infinidysk_core::blob::encode_meta(&meta).unwrap();
        let provenance=serde_json::json!({"version":1,"size":1,"fingerprint":Sha256::digest(meta.content_identity().unwrap()).to_vec()}).to_string();
        db.import_items_with_sources(
            &[(item.clone(), infinidysk_db::HealthStatus::default())],
            &[(blob, &raw)],
            &[(item.id, provenance)],
        )
        .await
        .unwrap();
        let remote = Arc::new(Remote::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::new_v4(),
            name: "fixture".into(),
            profile: arr::Profile::Radarr,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "fixture-key".into(),
        };
        let router = Router::new()
            .fallback(handle)
            .with_state(Arc::clone(&remote));
        let task = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        let request=serde_json::json!({"Id":uuid::Uuid::new_v4(),"Status":"requested","ArrHost":arr.url,"ArrMediaKind":"movie","ArrFileId":7,"ArrMediaIds":"3","LibraryPath":"/fixture/file.mkv","DavItemId":item.id.0.to_string()}).to_string();
        db.retain_legacy_repair_request("retire", Some(item.id), "arr-request", &request, None)
            .await
            .unwrap();
        let (_arr, arrs) = watch::channel(vec![arr]);
        let (_settings, settings) = watch::channel(super::super::Settings::default());
        let client = Arc::new(infinidysk_nntp::Client::new(
            Vec::<infinidysk_nntp::Provider>::new(),
        ));
        let reader = Arc::new(AtomicUsize::new(1));
        let cache = Arc::new(AtomicUsize::new(0));
        let authority = Arc::new(AtomicUsize::new(0));
        let health = Health::new(
            db.clone(),
            Arc::clone(&client),
            arrs.clone(),
            settings.clone(),
        )
        .unwrap()
        .with_read_gate({
            let reader = Arc::clone(&reader);
            move |_| {
                (reader.load(Ordering::SeqCst) == 0).then(|| Box::new(()) as super::super::ReadHold)
            }
        })
        .with_cached_copy(Arc::new({
            let cache = Arc::clone(&cache);
            move |_| {
                let state = cache.load(Ordering::SeqCst);
                Box::pin(async move {
                    match state {
                        0 => None,
                        1 => Some(true),
                        _ => Some(false),
                    }
                })
            }
        }))
        .with_repair_authority(Arc::new({
            let authority = Arc::clone(&authority);
            move |_, _| {
                let allow = authority.load(Ordering::SeqCst) > 0;
                Box::pin(async move { Ok(allow) })
            }
        }));
        assert_eq!(
            health.observe_legacy_repairs(i64::MAX, 10).await.unwrap(),
            0
        );
        assert_eq!(health.retire_legacy_sources(i64::MAX, 10).await.unwrap(), 0);
        remote.replaced.store(1, Ordering::SeqCst);
        assert_eq!(
            health.observe_legacy_repairs(i64::MAX, 10).await.unwrap(),
            1
        );
        assert_eq!(
            health.retire_legacy_sources(i64::MAX, 10).await.unwrap(),
            0,
            "reader protects source"
        );
        reader.store(0, Ordering::SeqCst);
        health.readers_closed(item.id).await.unwrap();
        assert_eq!(
            db.legacy_retirement_candidates(0, 10).await.unwrap().len(),
            1,
            "last reader clears the retry interval"
        );
        assert_eq!(
            health.retire_legacy_sources(i64::MAX, 10).await.unwrap(),
            0,
            "uncertain cache protects source"
        );
        cache.store(1, Ordering::SeqCst);
        assert_eq!(
            health.retire_legacy_sources(i64::MAX, 10).await.unwrap(),
            0,
            "complete retained copy protects source"
        );
        cache.store(2, Ordering::SeqCst);
        assert_eq!(
            health.retire_legacy_sources(i64::MAX, 10).await.unwrap(),
            0,
            "read-only root lacks authority"
        );
        authority.store(1, Ordering::SeqCst);
        remote.replaced.store(0, Ordering::SeqCst);
        assert_eq!(
            health.retire_legacy_sources(i64::MAX, 10).await.unwrap(),
            0,
            "stale replacement proof cannot retire source"
        );
        remote.replaced.store(1, Ordering::SeqCst);
        assert_eq!(health.retire_legacy_sources(i64::MAX, 10).await.unwrap(), 1);
        assert!(db.item(item.id).await.unwrap().is_none());
        assert!(
            db.legacy_repair_held(item.id).await.unwrap(),
            "original evidence remains"
        );
        drop(health);
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        let health = Health::new(db.clone(), client, arrs, settings)
            .unwrap()
            .with_read_gate(|_| Some(Box::new(())));
        assert_eq!(health.retire_legacy_sources(i64::MAX, 10).await.unwrap(), 0);
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 0);
        assert_eq!(remote.searches.load(Ordering::SeqCst), 0);
        task.abort();
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
    #[tokio::test]
    async fn legacy_evidence_blocks_manual_and_unowned_replacement_without_new_intent() {
        let path =
            std::env::temp_dir().join(format!("id-held-health-{}.sqlite", uuid::Uuid::new_v4()));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .unwrap();
        let root = db
            .item(infinidysk_core::ItemId::CONTENT)
            .await
            .unwrap()
            .unwrap();
        let item = Item::child(&root, "fixture.mkv", ItemKind::NzbFile).unwrap();
        db.insert(std::slice::from_ref(&item), &[]).await.unwrap();
        db.retain_legacy_repair_hold(
            "fixture-old-intent",
            Some(item.id),
            "arr-request",
            "uncertain",
        )
        .await
        .unwrap();
        let (_arr, arrs) = watch::channel(vec![]);
        let (_settings, settings) = watch::channel(super::super::Settings::default());
        let client = Arc::new(infinidysk_nntp::Client::new(
            Vec::<infinidysk_nntp::Provider>::new(),
        ));
        let health = Health::new(db.clone(), client, arrs, settings).unwrap();
        assert!(!health.repair_allowed(item.id, None).await.unwrap());
        assert!(health.request_repair(item.id).await.is_err());
        assert!(db.regrab_intent(item.id).await.unwrap().is_none());
        assert!(db.item(item.id).await.unwrap().is_some());
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
    #[tokio::test]
    async fn restart_recovers_deleted_arr_target_and_uncertain_search_without_replaying() {
        let path =
            std::env::temp_dir().join(format!("infinidysk-regrab-{}.sqlite", uuid::Uuid::new_v4()));
        let url = format!("sqlite:{}", path.display());
        let db = Db::connect(&url).await.unwrap();
        let root = db
            .item(infinidysk_core::ItemId::CONTENT)
            .await
            .unwrap()
            .unwrap();
        let category = Item::child(&root, "fixture", ItemKind::Directory).unwrap();
        let job = Item::child(&category, "job", ItemKind::Directory).unwrap();
        let mut item = Item::child(&job, "file.mkv", ItemKind::NzbFile).unwrap();
        let blob = BlobId::new();
        item.meta_blob = Some(blob);
        item.size = Some(1);
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: "fixture@invalid".into(),
                    size: 1,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                ..Default::default()
            },
            proof: None,
        });
        db.insert(
            &[category, job, item.clone()],
            &[(blob, &infinidysk_core::blob::encode_meta(&meta).unwrap())],
        )
        .await
        .unwrap();
        let remote = Arc::new(Remote::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::new_v4(),
            name: "fixture".into(),
            profile: arr::Profile::Radarr,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "fixture-secret".into(),
        };
        let router = Router::new()
            .fallback(handle)
            .with_state(Arc::clone(&remote));
        let task = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        db.record_owner(arr.id, item.id, Some(7), "/fixture/file.mkv", None)
            .await
            .unwrap();
        let (arr_tx, arrs) = watch::channel(vec![arr]);
        let (_tx, settings) = watch::channel(super::super::Settings::default());
        let client = Arc::new(infinidysk_nntp::Client::new(
            Vec::<infinidysk_nntp::Provider>::new(),
        ));
        // A policy change between preflight and the durable mutation checkpoint
        // must still prevent the actual Arr write.
        let admissions = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let checks = Arc::clone(&admissions);
        let guarded = Health::new(
            db.clone(),
            Arc::clone(&client),
            arrs.clone(),
            settings.clone(),
        )
        .unwrap()
        .with_repair_authority(Arc::new(move |_, path| {
            let checks = Arc::clone(&checks);
            Box::pin(async move {
                Ok(matches!(path, super::super::RepairTarget::Inspect)
                    || checks.fetch_add(1, Ordering::SeqCst) == 0)
            })
        }));
        guarded.request_repair(item.id).await.unwrap();
        guarded.step().await.unwrap();
        assert!(admissions.load(Ordering::SeqCst) >= 2);
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 0);
        assert_eq!(remote.searches.load(Ordering::SeqCst), 0);
        assert!(db.item(item.id).await.unwrap().is_some());
        drop(guarded);
        let health = Health::new(
            db.clone(),
            Arc::clone(&client),
            arrs.clone(),
            settings.clone(),
        )
        .unwrap()
        .with_arr_wait(std::time::Duration::ZERO);
        health.request_repair(item.id).await.unwrap();
        health.step().await.unwrap();
        assert!(db.item(item.id).await.unwrap().is_some());
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 1);
        let row = db.regrab_intent(item.id).await.unwrap().unwrap();
        assert_eq!(
            serde_json::from_str::<Saved>(&row.payload)
                .unwrap()
                .progress
                .stage,
            RegrabStage::DeletePending
        );
        assert!(!row.payload.contains("fixture-secret"));
        drop(health);
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        let health = Health::new(db.clone(), client, arrs.clone(), settings)
            .unwrap()
            .with_arr_wait(std::time::Duration::ZERO);
        for _ in 0..2 {
            let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
            status.next_check = Some(0);
            db.set_health(item.id, &status, Recheck::None)
                .await
                .unwrap();
            assert!(health.step().await.unwrap());
        }
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 1);
        assert_eq!(remote.searches.load(Ordering::SeqCst), 1);
        assert_eq!(
            db.health(item.id).await.unwrap().unwrap().1.state,
            HealthState::Hidden
        );
        assert_eq!(
            serde_json::from_str::<Saved>(
                &db.regrab_intent(item.id).await.unwrap().unwrap().payload
            )
            .unwrap()
            .progress
            .stage,
            RegrabStage::Submitted
        );
        let row = db.regrab_intent(item.id).await.unwrap().unwrap();
        assert!(
            !health
                .control_regrab(item.id, row.revision - 1, Control::Abandon)
                .await
                .unwrap()
        );
        assert!(
            health
                .control_regrab(item.id, row.revision, Control::Abandon)
                .await
                .unwrap()
        );
        let row = db.regrab_intent(item.id).await.unwrap().unwrap();
        assert!(row.terminal);
        assert!(
            serde_json::from_str::<Saved>(&row.payload)
                .unwrap()
                .abandoned
        );
        assert_eq!(health.observe_regrabs(i64::MAX, 10).await.unwrap(), 0);
        assert!(
            health
                .control_regrab(item.id, row.revision, Control::Resume)
                .await
                .unwrap()
        );
        let row = db.regrab_intent(item.id).await.unwrap().unwrap();
        assert!(!row.terminal);
        assert_eq!(
            serde_json::from_str::<Saved>(&row.payload)
                .unwrap()
                .progress
                .stage,
            RegrabStage::Submitted
        );
        db.delete_item(item.id).await.unwrap();
        let configured = arrs.borrow().clone();
        arr_tx.send(vec![]).unwrap();
        assert_eq!(health.observe_regrabs(i64::MAX, 10).await.unwrap(), 1);
        assert!(!db.regrab_intent(item.id).await.unwrap().unwrap().terminal);
        arr_tx.send(configured).unwrap();
        remote.replaced.store(1, Ordering::SeqCst);
        assert_eq!(health.observe_regrabs(i64::MAX, 10).await.unwrap(), 1);
        let row = db.regrab_intent(item.id).await.unwrap().unwrap();
        assert!(row.terminal);
        let saved: Saved = serde_json::from_str(&row.payload).unwrap();
        assert_eq!(saved.progress.stage, RegrabStage::Replaced);
        assert!(saved.observed_at.is_some());
        assert_eq!(remote.deletes.load(Ordering::SeqCst), 1);
        assert_eq!(remote.searches.load(Ordering::SeqCst), 1);
        assert!(
            health
                .control_regrab(item.id, row.revision, Control::Resume)
                .await
                .is_err()
        );
        task.abort();
        drop(health);
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
}
