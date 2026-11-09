//! Original download recovery never authorizes a blocklist from a guessed job id.
#![allow(clippy::unwrap_used)] // Fixture setup failures are test failures.
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Json;
use axum::extract::{Request, State};
use axum::http::StatusCode;
use infinidysk_arr::{Arr, Profile, owner_of_file, replace};
use serde_json::{Value, json};
use tokio::net::TcpListener;
use tokio::task::JoinHandle;

#[derive(Clone)]
struct History {
    pages: Vec<Value>,
    fail_page: Option<usize>,
    calls: Arc<Mutex<Vec<String>>>,
}
struct Fixture {
    arr: Arr,
    state: History,
    task: JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    async fn start(pages: Vec<Value>, fail_page: Option<usize>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::nil(),
            name: "test".to_owned(),
            profile: Profile::Radarr,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "test-key".to_owned(),
        };
        let state = History {
            pages,
            fail_page,
            calls: Arc::default(),
        };
        let app = axum::Router::new()
            .fallback(answer)
            .with_state(state.clone());
        let task = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        Self { arr, state, task }
    }
}
async fn answer(State(s): State<History>, req: Request) -> (StatusCode, Json<Value>) {
    s.calls
        .lock()
        .unwrap()
        .push(format!("{} {}", req.method(), req.uri()));
    let query = req.uri().query().unwrap_or("");
    let body = match req.uri().path() {
        "/api/v3/moviefile/7" => json!({"id":7,"movieId":3,"path":"/library/file.mkv"}),
        "/api/v3/history" if query.contains("eventType=3") => {
            // Singular movieId is ignored by Radarr. A global response must never resolve.
            if !query.split('&').any(|p| p == "movieIds=3") {
                return (
                    StatusCode::OK,
                    Json(json!({"records":[],"totalRecords":20_000})),
                );
            }
            let page: usize = query
                .split('&')
                .find_map(|p| p.strip_prefix("page="))
                .unwrap()
                .parse()
                .unwrap();
            if s.fail_page == Some(page) {
                return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({})));
            }
            s.pages
                .get(page - 1)
                .cloned()
                .unwrap_or_else(|| json!({"records":[],"totalRecords":0}))
        }
        "/api/v3/history" => json!({"records":[{"id":99}]}),
        "/api/v3/queue/details" => json!([]),
        _ => json!({}),
    };
    (StatusCode::OK, Json(body))
}
fn record(id: i64, download: &str, file: &Value, path: &str) -> Value {
    json!({"id":id,"movieId":3,"downloadId":download,"data":{"fileId":file,"importedPath":path}})
}

#[tokio::test]
async fn complete_history_recovers_original_and_ignores_migration_id() {
    let mut first: Vec<_> = (1..=100)
        .map(|i| record(i, "unrelated", &json!(8), "/library/old.mkv"))
        .collect();
    first[0] = record(1, "original", &json!("7"), "/library/file.mkv");
    let f = Fixture::start(vec![json!({"records":first,"totalRecords":101}),
        json!({"records":[record(101,"ORIGINAL",&json!(7),"/library/file.mkv")],"totalRecords":101})],None).await;
    let http = reqwest::Client::new();
    let owner = owner_of_file(&http, &f.arr, 7).await.unwrap().unwrap();
    assert_eq!(owner.download_id(), Some("original"));
    replace(&http, &f.arr, Some("migration-id"), &owner, Duration::ZERO)
        .await
        .unwrap();
    let calls = f.state.calls.lock().unwrap().clone();
    assert!(
        calls
            .iter()
            .any(|c| c.contains("movieIds=3") && c.contains("page=2"))
    );
    assert!(
        calls
            .iter()
            .any(|c| c.contains("downloadId=original&eventType=1"))
    );
    assert!(!calls.iter().any(|c| c.contains("migration-id")));
    assert!(calls.iter().any(|c| c == "POST /api/v3/history/failed/99"));
}

#[tokio::test]
async fn path_or_file_id_alone_does_not_resolve_provenance() {
    let f=Fixture::start(vec![json!({"records":[record(1,"wrong-file",&json!(8),"/library/file.mkv"),
        record(2,"wrong-path",&json!(7),"/library/old.mkv"),record(3,"no-id",&Value::Null,"/library/file.mkv")],"totalRecords":3})],None).await;
    let http = reqwest::Client::new();
    let owner = owner_of_file(&http, &f.arr, 7).await.unwrap().unwrap();
    assert_eq!(owner.download_id(), None);
    replace(&http, &f.arr, Some("migration-id"), &owner, Duration::ZERO)
        .await
        .unwrap();
    assert!(
        !f.state
            .calls
            .lock()
            .unwrap()
            .iter()
            .any(|c| c.contains("history/failed") || c.contains("migration-id"))
    );
}

#[tokio::test]
async fn conflicting_downloads_for_the_same_current_file_stop_repair() {
    let f=Fixture::start(vec![json!({"records":[record(1,"first",&json!(7),"/library/file.mkv"),record(2,"second",&json!(7),"/library/file.mkv")],"totalRecords":2})],None).await;
    assert!(
        owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .is_err()
    );
    assert!(
        f.state
            .calls
            .lock()
            .unwrap()
            .iter()
            .all(|c| c.starts_with("GET "))
    );
}

#[tokio::test]
async fn failed_later_page_cannot_authorize_recovery_from_the_first() {
    let first: Vec<_> = (1..=100)
        .map(|i| record(i, "first", &json!(7), "/library/file.mkv"))
        .collect();
    let f = Fixture::start(vec![json!({"records":first,"totalRecords":101})], Some(2)).await;
    assert!(
        owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .is_err()
    );
    assert!(
        f.state
            .calls
            .lock()
            .unwrap()
            .iter()
            .all(|c| c.starts_with("GET "))
    );
}

#[tokio::test]
async fn short_page_before_the_advertised_total_is_incomplete() {
    let f = Fixture::start(
        vec![json!({"records":[record(1,"first",&json!(7),"/library/file.mkv")],"totalRecords":2})],
        None,
    )
    .await;
    assert!(
        owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .is_err()
    );
}

#[tokio::test]
async fn changing_totals_cannot_authorize_a_blocklist() {
    let first: Vec<_> = (1..=100)
        .map(|i| record(i, "first", &json!(7), "/library/file.mkv"))
        .collect();
    let f = Fixture::start(
        vec![
            json!({"records":first,"totalRecords":101}),
            json!({"records":[],"totalRecords":100}),
        ],
        None,
    )
    .await;
    assert!(
        owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .is_err()
    );
}

#[tokio::test]
async fn a_repeated_page_is_not_mistaken_for_complete_history() {
    let first: Vec<_> = (1..=100)
        .map(|i| record(i, "first", &json!(7), "/library/file.mkv"))
        .collect();
    let f = Fixture::start(
        vec![
            json!({"records":first.clone(),"totalRecords":200}),
            json!({"records":first,"totalRecords":200}),
        ],
        None,
    )
    .await;
    assert!(
        owner_of_file(&reqwest::Client::new(), &f.arr, 7)
            .await
            .is_err()
    );
}
