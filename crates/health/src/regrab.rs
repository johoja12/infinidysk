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
impl Health {
    pub(super) async fn tracked_regrab(
        &self,
        item: &Item,
        arr: &Arr,
        owner: &Owner,
        job_name: &str,
        existing: Option<RegrabIntent>,
    ) -> Result<Result<(), arr::Error>, Error> {
        let expected = scope(&self.db, item, arr).await?;
        let initial = Saved {
            arr_id: arr.id.to_string(),
            job_name: job_name.to_owned(),
            progress: Regrab::new(owner),
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
        if infinidysk_db::now_ms().saturating_sub(row.created_at) > super::DAY_MS
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
        let configuration = self.arr.clone();
        let readers = &self.in_use;
        let checkpoint = move |progress: Regrab| {
            let (db, scope, version, arr_id, job_name) = (
                db.clone(),
                expected.clone(),
                Arc::clone(&version),
                arr_id.clone(),
                name.clone(),
            );
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
            "/api/v3/moviefile/7"=>serde_json::json!({"id":7,"movieId":3,"path":"/fixture/file.mkv"}),
            "/api/v3/movie/3"=>serde_json::json!({"movieFile":if s.deletes.load(Ordering::SeqCst)>0 {serde_json::Value::Null}else{serde_json::json!({"id":7,"path":"/fixture/file.mkv"})}}),
            "/api/v3/history"=>serde_json::json!({"records":[],"totalRecords":0}),
            "/api/v3/queue/details"=>serde_json::json!([]),
            "/api/v3/command"=>if s.searches.load(Ordering::SeqCst)>0 {serde_json::json!([{"id":11,"body":{"name":"MoviesSearch","movieIds":[3]}}])}else{serde_json::json!([])},
            _=>panic!("unexpected fixture endpoint {method} {path}"),
        }).into_response()
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
        let (_tx, arrs) = watch::channel(vec![arr]);
        let (_tx, settings) = watch::channel(super::super::Settings::default());
        let client = Arc::new(infinidysk_nntp::Client::new(
            Vec::<infinidysk_nntp::Provider>::new(),
        ));
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
        let health = Health::new(db.clone(), client, arrs, settings)
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
        task.abort();
        drop(health);
        db.close().await;
        let _ = std::fs::remove_file(path);
    }
}
