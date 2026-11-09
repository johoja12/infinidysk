//! Repair behavior carried from the 1.x fork. All requests stay on loopback.
#![allow(clippy::unwrap_used)] // Fixture helpers panic on setup failures, as test assertions do.
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Json;
use axum::extract::{Request, State};
use axum::http::StatusCode;
use axum::response::IntoResponse;
use infinidysk_arr::{Arr, Profile, owner, owner_of_file, replace};
use serde_json::json;
use tokio::net::TcpListener;
use tokio::task::JoinHandle;

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum Queue {
    #[default]
    Empty,
    Existing,
    AfterBlocklist,
}

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum Failure {
    #[default]
    None,
    Blocklist,
    Delete,
}

#[derive(Clone, Default)]
struct Scenario {
    calls: Arc<Mutex<Vec<String>>>,
    grabbed: bool,
    queue: Queue,
    replaced: bool,
    failure: Failure,
}

struct Server {
    arr: Arr,
    scenario: Scenario,
    task: JoinHandle<()>,
}

impl Drop for Server {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl Server {
    async fn start(profile: Profile, scenario: Scenario) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let arr = Arr {
            id: uuid::Uuid::nil(),
            name: "test-arr".to_owned(),
            profile,
            url: format!("http://{}", listener.local_addr().unwrap()),
            api_key: "test-key".to_owned(),
        };
        let app = axum::Router::new()
            .fallback(answer)
            .with_state(scenario.clone());
        let task = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        Self {
            arr,
            scenario,
            task,
        }
    }

    fn calls(&self) -> Vec<String> {
        self.scenario.calls.lock().unwrap().clone()
    }
}

async fn answer(State(s): State<Scenario>, req: Request) -> impl IntoResponse {
    let path = req.uri().path();
    s.calls
        .lock()
        .unwrap()
        .push(format!("{} {}", req.method(), req.uri()));
    if (path == "/api/v3/history/failed/11" && s.failure == Failure::Blocklist)
        || (req.method() == "DELETE" && s.failure == Failure::Delete)
    {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({})));
    }
    let imported = req.uri().query().is_some_and(|q| q.contains("eventType=3"));
    let data = match path {
        "/api" => json!({"current":"v3", "deprecated":[]}),
        "/api/v3/system/status" => json!({"appName": "Whisparr", "version": "3.0.0"}),
        "/api/v3/history" if imported => json!({"records": [{
            "id": 22, "movieId": 3, "episodeId": 5,
            "data": {"droppedPath": "/downloads/tv/job/file.mkv", "importedPath": "/library/file.mkv"}
        }]}),
        "/api/v3/history" => {
            json!({"records": if s.grabbed { vec![json!({"id": 11})] } else { vec![] }})
        }
        "/api/v3/movie/3" => {
            json!({"movieFile": {"id": if s.replaced {8} else {7}, "path": "/library/file.mkv"}})
        }
        "/api/v3/episode/5" => json!({"episodeFileId": if s.replaced {8} else {7}}),
        "/api/v3/episodefile/7" | "/api/v3/moviefile/7" => {
            json!({"id": 7, "movieId": 3, "path": "/library/file.mkv"})
        }
        "/api/v3/episodefile/8" => json!({"id": 8, "path": "/library/file.mkv"}),
        "/api/v3/queue/details" => json!(if s.queue == Queue::Existing
            || (s.queue == Queue::AfterBlocklist
                && s.calls
                    .lock()
                    .unwrap()
                    .iter()
                    .any(|c| c.contains("history/failed")))
        {
            vec![json!({"downloadId": "replacement"})]
        } else {
            vec![]
        }),
        _ => json!({}),
    };
    (StatusCode::OK, Json(data))
}

async fn imported_owner(server: &Server, http: &reqwest::Client) -> infinidysk_arr::Owner {
    owner(
        http,
        &server.arr,
        "original",
        &["/tv/job/file.mkv".to_owned()],
        Some(7),
    )
    .await
    .unwrap()
    .unwrap()
}

async fn missing_grab(profile: Profile, kind: &str) {
    let server = Server::start(profile, Scenario::default()).await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    // No failed-history call can start an Arr search, so its grace period serves no purpose.
    tokio::time::timeout(
        Duration::from_secs(2),
        replace(
            &http,
            &server.arr,
            Some("original"),
            &owner,
            Duration::from_secs(60),
        ),
    )
    .await
    .expect("missing grab must not wait for an automatic search")
    .unwrap();
    let calls = server.calls();
    assert!(!calls.iter().any(|c| c.contains("history/failed")));
    assert_eq!(
        calls.iter().filter(|c| c.contains("queue/details")).count(),
        1
    );
    assert_eq!(
        &calls[calls.len() - 2..],
        [
            format!("DELETE /api/v3/{kind}/7"),
            "POST /api/v3/command".to_owned()
        ]
    );
}

#[tokio::test]
async fn radarr_missing_grab_searches_without_waiting_or_blocklisting() {
    missing_grab(Profile::Radarr, "moviefile").await;
}

#[tokio::test]
async fn sonarr_missing_grab_searches_without_waiting_or_blocklisting() {
    missing_grab(Profile::Sonarr, "episodefile").await;
}

#[tokio::test]
async fn whisparr_v3_missing_grab_searches_without_waiting_or_blocklisting() {
    missing_grab(Profile::Whisparr, "moviefile").await;
}

#[tokio::test]
async fn queued_replacement_prevents_destructive_calls() {
    let server = Server::start(
        Profile::Radarr,
        Scenario {
            queue: Queue::Existing,
            ..Scenario::default()
        },
    )
    .await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    replace(&http, &server.arr, Some("original"), &owner, Duration::ZERO)
        .await
        .unwrap();
    assert!(server.calls().iter().all(|c| c.starts_with("GET ")));
}

#[tokio::test]
async fn imported_replacement_prevents_destructive_calls() {
    let server = Server::start(
        Profile::Radarr,
        Scenario {
            replaced: true,
            ..Scenario::default()
        },
    )
    .await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    replace(&http, &server.arr, Some("original"), &owner, Duration::ZERO)
        .await
        .unwrap();
    assert!(server.calls().iter().all(|c| c.starts_with("GET ")));
}

#[tokio::test]
async fn failed_blocklist_does_not_delete_or_search() {
    let server = Server::start(
        Profile::Radarr,
        Scenario {
            grabbed: true,
            failure: Failure::Blocklist,
            ..Scenario::default()
        },
    )
    .await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    assert!(
        replace(&http, &server.arr, Some("original"), &owner, Duration::ZERO)
            .await
            .is_err()
    );
    assert!(
        server
            .calls()
            .iter()
            .all(|c| !c.starts_with("DELETE ") && !c.ends_with("/command"))
    );
}

#[tokio::test]
async fn failed_delete_does_not_start_a_search() {
    let server = Server::start(
        Profile::Radarr,
        Scenario {
            failure: Failure::Delete,
            ..Scenario::default()
        },
    )
    .await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    assert!(
        replace(&http, &server.arr, Some("original"), &owner, Duration::ZERO)
            .await
            .is_err()
    );
    assert!(!server.calls().iter().any(|c| c.ends_with("/command")));
}

#[tokio::test]
async fn file_owner_without_a_download_deletes_and_searches_without_blocklisting() {
    let server = Server::start(Profile::Radarr, Scenario::default()).await;
    let http = reqwest::Client::new();
    let owner = owner_of_file(&http, &server.arr, 7).await.unwrap().unwrap();
    replace(&http, &server.arr, None, &owner, Duration::from_secs(60))
        .await
        .unwrap();
    let calls = server.calls();
    assert!(
        !calls
            .iter()
            .any(|c| c.contains("history/failed") || c.contains("downloadId="))
    );
    assert_eq!(
        &calls[calls.len() - 2..],
        ["DELETE /api/v3/moviefile/7", "POST /api/v3/command"]
    );
}

#[tokio::test]
async fn successful_blocklist_still_waits_for_the_arrs_automatic_replacement() {
    let server = Server::start(
        Profile::Radarr,
        Scenario {
            grabbed: true,
            queue: Queue::AfterBlocklist,
            ..Scenario::default()
        },
    )
    .await;
    let http = reqwest::Client::new();
    let owner = imported_owner(&server, &http).await;
    replace(
        &http,
        &server.arr,
        Some("original"),
        &owner,
        Duration::from_secs(6),
    )
    .await
    .unwrap();
    let calls = server.calls();
    assert!(calls.iter().any(|c| c == "POST /api/v3/history/failed/11"));
    assert_eq!(
        calls.iter().filter(|c| c.contains("queue/details")).count(),
        2
    );
    assert!(
        calls
            .iter()
            .all(|c| !c.starts_with("DELETE ") && !c.ends_with("/command"))
    );
}
