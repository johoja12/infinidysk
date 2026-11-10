//! External writes may succeed despite a failed response. Resume only by observation.
#![allow(clippy::unwrap_used)] // Fixture setup errors are assertions.
use axum::{
    Json, Router,
    extract::{Request, State},
    http::StatusCode,
    response::{IntoResponse, Response},
};
use infinidysk_arr::{Arr, Error, Profile, Regrab, RegrabStage, owner_of_file, replace_tracked};
use serde_json::{Value, json};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Duration;
#[derive(Default)]
struct Data {
    mode: AtomicUsize,
    deleted: AtomicUsize,
    replaced: AtomicUsize,
    search: AtomicUsize,
    calls: Mutex<Vec<String>>,
}
struct Fixture {
    state: Arc<Data>,
    arr: Arr,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    async fn new() -> Self {
        let state = Arc::new(Data::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::new_v4(),
            name: "fixture".into(),
            profile: Profile::Radarr,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "fixture".into(),
        };
        let app = Router::new()
            .fallback(handler)
            .with_state(Arc::clone(&state));
        let task = tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });
        Self { state, arr, task }
    }
    fn count(&self, prefix: &str) -> usize {
        self.state
            .calls
            .lock()
            .unwrap()
            .iter()
            .filter(|v| v.starts_with(prefix))
            .count()
    }
}
async fn handler(State(s): State<Arc<Data>>, request: Request) -> Response {
    let p = request.uri().path();
    let method = request.method().as_str();
    s.calls.lock().unwrap().push(format!("{method} {p}"));
    let mode = s.mode.load(Ordering::SeqCst);
    if p.ends_with("/moviefile/7") && method == "DELETE" {
        if mode != 2 {
            s.deleted.store(1, Ordering::SeqCst);
        }
        if mode == 1 || mode == 2 {
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
        return Json(json!({})).into_response();
    }
    if p.ends_with("/command") {
        if method == "POST" {
            s.search.fetch_add(1, Ordering::SeqCst);
            if mode == 3 {
                return StatusCode::INTERNAL_SERVER_ERROR.into_response();
            }
            return Json(json!({"id":11})).into_response();
        }
        if mode == 8 {
            return Json(
                json!([{"id":42,"name":"MoviesSearch","status":"started","body":{"movieIds":[3]}}]),
            )
            .into_response();
        }
        return Json(if s.search.load(Ordering::SeqCst) > 0 {
            json!([{"id":11,"body":{"name":"MoviesSearch","movieIds":[3]}}])
        } else {
            json!([])
        })
        .into_response();
    }
    Json(match p {
        "/api/v3/rootfolder"=>if mode==4 {json!([{ "path":"/elsewhere" }])}else if mode==5 {json!([{ "path":"/library/../outside" }])}else if mode==6 {json!([{ "path":"/lib" }])}else if mode==7 {json!([{ "path":"/" }])}else{json!([{ "path":"/library" }])},
        "/api/v3/moviefile/7"=>json!({"id":7,"movieId":3,"path":"/library/file.mkv"}),
        "/api/v3/movie/3"=>json!({"movieFile":if s.replaced.load(Ordering::SeqCst)>0 {json!({"id":8,"path":"/library/replacement.mkv"})}else if s.deleted.load(Ordering::SeqCst)>0 {Value::Null}else{json!({"id":7,"path":"/library/file.mkv"})}}),
        "/api/v3/queue/details"=>json!([]),
        "/api/v3/history"=>json!({"records":[],"totalRecords":0}),
        _=>panic!("unexpected fixture request {method} {p}"),
    }).into_response()
}
async fn progress(f: &Fixture) -> Regrab {
    Regrab::new(
        &owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .unwrap()
            .unwrap(),
    )
}
fn recorder() -> Arc<Mutex<Option<String>>> {
    Arc::new(Mutex::new(None))
}
async fn run(f: &Fixture, p: &mut Regrab, saved: &Arc<Mutex<Option<String>>>) -> Result<(), Error> {
    let saved = Arc::clone(saved);
    replace_tracked(
        &reqwest::Client::new(),
        &f.arr,
        p,
        Duration::ZERO,
        move |p| {
            let saved = Arc::clone(&saved);
            async move {
                *saved.lock().unwrap() = Some(serde_json::to_string(&p).unwrap());
                Ok(())
            }
        },
    )
    .await
}
#[tokio::test]
async fn deletion_that_succeeded_but_returned_500_resumes_after_restart_without_redelete() {
    let f = Fixture::new().await;
    let mut p = progress(&f).await;
    let saved = recorder();
    f.state.mode.store(1, Ordering::SeqCst);
    assert!(run(&f, &mut p, &saved).await.is_err());
    assert_eq!(p.stage, RegrabStage::DeletePending);
    let mut restored = serde_json::from_str(saved.lock().unwrap().as_ref().unwrap()).unwrap();
    f.state.mode.store(0, Ordering::SeqCst);
    run(&f, &mut restored, &saved).await.unwrap();
    assert_eq!(restored.stage, RegrabStage::Submitted);
    assert_eq!(f.count("DELETE"), 1);
    assert_eq!(f.state.search.load(Ordering::SeqCst), 1);
}
#[tokio::test]
async fn deletion_not_observed_is_never_blindly_replayed() {
    let f = Fixture::new().await;
    let mut p = progress(&f).await;
    let saved = recorder();
    f.state.mode.store(2, Ordering::SeqCst);
    assert!(run(&f, &mut p, &saved).await.is_err());
    assert!(matches!(
        run(&f, &mut p, &saved).await,
        Err(Error::Uncertain(_))
    ));
    assert_eq!(f.count("DELETE"), 1);
    assert_eq!(f.state.search.load(Ordering::SeqCst), 0);
}
#[tokio::test]
async fn accepted_search_with_failed_response_is_reconciled_without_reposting() {
    let f = Fixture::new().await;
    let mut p = progress(&f).await;
    let saved = recorder();
    f.state.mode.store(3, Ordering::SeqCst);
    assert!(run(&f, &mut p, &saved).await.is_err());
    assert_eq!(p.stage, RegrabStage::SearchPending);
    let mut restored = serde_json::from_str(saved.lock().unwrap().as_ref().unwrap()).unwrap();
    run(&f, &mut restored, &saved).await.unwrap();
    assert_eq!(restored.stage, RegrabStage::Submitted);
    assert_eq!(f.state.search.load(Ordering::SeqCst), 1);
}
#[tokio::test]
async fn replaced_target_and_failed_checkpoint_cannot_delete_a_file() {
    let f = Fixture::new().await;
    let mut p = progress(&f).await;
    assert!(
        replace_tracked(
            &reqwest::Client::new(),
            &f.arr,
            &mut p,
            Duration::ZERO,
            |_| async { Err(Error::Checkpoint) }
        )
        .await
        .is_err()
    );
    assert_eq!(f.count("DELETE"), 0);
    f.state.replaced.store(1, Ordering::SeqCst);
    run(&f, &mut p, &recorder()).await.unwrap();
    assert_eq!(p.stage, RegrabStage::Replaced);
    assert_eq!(f.count("DELETE"), 0);
}

#[tokio::test]
async fn current_library_authority_is_required_before_any_mutation() {
    for mode in [4, 5, 6, 7] {
        let f = Fixture::new().await;
        let mut p = progress(&f).await;
        f.state.mode.store(mode, Ordering::SeqCst);
        let saved = recorder();
        assert!(run(&f, &mut p, &saved).await.is_err());
        assert_eq!(f.count("DELETE"), 0);
        assert_eq!(f.count("POST"), 0);
        assert_eq!(p.stage, RegrabStage::Prepared);
    }
}

#[tokio::test]
async fn durable_blocklist_resume_waits_for_own_search_without_deleting_or_reblocking() {
    let f = Fixture::new().await;
    let original = progress(&f).await;
    let mut value = serde_json::to_value(original).unwrap();
    value["owner"]["download_id"] = json!("original");
    value["stage"] = json!("blocklisted");
    // This is the pre-beta7 journal shape, with no search baseline.
    value.as_object_mut().unwrap().remove("search_since");
    let mut resumed: Regrab = serde_json::from_value(value).unwrap();
    let saved = recorder();
    f.state.mode.store(8, Ordering::SeqCst);
    assert!(matches!(
        run(&f, &mut resumed, &saved).await,
        Err(Error::Searching)
    ));
    assert_eq!(resumed.stage, RegrabStage::Blocklisted);
    assert_eq!(f.count("DELETE"), 0);
    assert_eq!(f.count("POST"), 0);
    let mut restored: Regrab =
        serde_json::from_str(&serde_json::to_string(&resumed).unwrap()).unwrap();
    f.state.mode.store(0, Ordering::SeqCst);
    run(&f, &mut restored, &saved).await.unwrap();
    assert_eq!(restored.stage, RegrabStage::Submitted);
    assert_eq!(f.count("DELETE"), 1);
    assert_eq!(f.count("POST /api/v3/history/failed"), 0);
}

#[tokio::test]
async fn a_file_only_durable_repair_also_waits_for_an_active_own_search() {
    let f = Fixture::new().await;
    let mut p = progress(&f).await;
    assert!(p.owner.download_id().is_none());
    f.state.mode.store(8, Ordering::SeqCst);
    assert!(matches!(
        run(&f, &mut p, &recorder()).await,
        Err(Error::Searching)
    ));
    assert_eq!(p.stage, RegrabStage::Blocklisted);
    assert_eq!(f.count("DELETE"), 0);
    assert_eq!(f.count("POST"), 0);
}
