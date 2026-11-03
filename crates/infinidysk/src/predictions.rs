//! Complete, configuration-bound Plex snapshots. An outage never publishes partial
//! results or substitutes the owner credential for an unresolved viewer.
use crate::{
    admin::{Admin, Fail},
    config::{ConfigStore, Settings},
};
use axum::{Json, extract::State};
use infinidysk_core::ItemId;
use infinidysk_db::{Db, now_ms};
use infinidysk_plex::{
    Client, Error, MappedPath, Media, Server, ViewerServer, map_path, next_episodes,
};
use infinidysk_prefetch::{Automatic, Queue, Range};
use infinidysk_vfs::{Node, Vfs};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fmt::Write as _;
use std::{
    collections::{HashMap, HashSet},
    future::Future,
    pin::Pin,
    sync::{Arc, Mutex, PoisonError},
    time::Duration,
};
use tokio::sync::{Mutex as AsyncMutex, RwLock};
use utoipa::ToSchema;
use uuid::Uuid;
const STORE: &str = "fork_predictions_v1";
const MAX: usize = 100;
const MAX_BYTES: usize = 2 * 1024 * 1024;
pub(crate) type LocalResolver = Arc<
    dyn Fn(String) -> Pin<Box<dyn Future<Output = Result<Option<ItemId>, ()>> + Send>>
        + Send
        + Sync,
>;
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PlexPredictionSource)]
pub enum Source {
    Playback,
    NextEpisode,
    PartialMovie,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexPrediction)]
pub struct Prediction {
    pub id: Uuid,
    pub server: String,
    pub viewer: String,
    pub rating_key: String,
    pub title: String,
    pub source: Source,
    pub item: Option<Uuid>,
    pub revision: Option<String>,
    pub size: u64,
    pub ranges: Vec<Range>,
    pub warning: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
struct Saved {
    version: u8,
    scope: String,
    updated_at: i64,
    predictions: Vec<Prediction>,
}
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=PlexPredictionSnapshot)]
pub struct Snapshot {
    pub predictions: Vec<Prediction>,
    pub updated_at: Option<i64>,
    pub refreshing: bool,
    pub stale: bool,
    pub error: Option<String>,
    pub warning: Option<String>,
}
#[derive(Default)]
struct Current {
    saved: Option<Saved>,
    refreshing: bool,
    retry_at: i64,
    error: Option<String>,
    warning: Option<String>,
    restored: bool,
    attempt_scope: Option<String>,
}
struct Refreshing<'a>(&'a Mutex<Current>);
impl Drop for Refreshing<'_> {
    fn drop(&mut self) {
        self.0
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .refreshing = false;
    }
}
pub struct Predictions {
    db: Db,
    client: Client,
    config: Arc<RwLock<ConfigStore>>,
    vfs: Arc<Vfs>,
    queue: Arc<Queue>,
    local: LocalResolver,
    current: Mutex<Current>,
    refresh: AsyncMutex<()>,
}
impl Predictions {
    pub async fn load(
        db: Db,
        client: Client,
        config: Arc<RwLock<ConfigStore>>,
        vfs: Arc<Vfs>,
        queue: Arc<Queue>,
        local: LocalResolver,
    ) -> anyhow::Result<Arc<Self>> {
        let raw = db
            .config()
            .await?
            .into_iter()
            .find(|(k, _)| k == STORE)
            .map(|(_, v)| v);
        let mut current = Current::default();
        if let Some(raw) = raw {
            let saved = if raw.len() <= MAX_BYTES {
                serde_json::from_str::<Saved>(&raw).ok().filter(valid_saved)
            } else {
                None
            };
            current.warning = saved.is_none().then(|| {
                "Saved predictions could not be loaded; a fresh pass is needed".to_owned()
            });
            current.restored = saved.is_some();
            current.attempt_scope = saved.as_ref().map(|s| s.scope.clone());
            current.saved = saved;
        }
        Ok(Arc::new(Self {
            db,
            client,
            config,
            vfs,
            queue,
            local,
            current: Mutex::new(current),
            refresh: AsyncMutex::new(()),
        }))
    }
    pub async fn snapshot(&self) -> Snapshot {
        let settings = self.config.read().await.settings().clone();
        let scope = scope(&settings);
        let state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
        let saved = state.saved.as_ref().filter(|s| s.scope == scope);
        Snapshot {
            predictions: saved.map_or_else(Vec::new, |s| s.predictions.clone()),
            updated_at: saved.map(|s| s.updated_at),
            refreshing: state.refreshing,
            stale: state.restored
                || state.error.is_some()
                || saved.is_none_or(|s| {
                    now_ms().saturating_sub(s.updated_at)
                        >= i64::from(settings.prefetch.plex.refresh_seconds) * 1000
                }),
            error: state.error.clone(),
            warning: state.warning.clone(),
        }
    }
    /// One bounded shared pass. Request cancellation cannot own or kill a background pass.
    pub async fn tick(&self, force: bool) -> anyhow::Result<()> {
        let Ok(_refresh) = self.refresh.try_lock() else {
            return Ok(());
        };
        let settings = self.config.read().await.settings().clone();
        let expected = scope(&settings);
        self.queue
            .reconcile_automatic(automatic_scope(&settings).as_deref(), None)
            .await?;
        {
            let mut state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            let changed = state.attempt_scope.as_deref() != Some(expected.as_str());
            if !force && !changed && state.retry_at > now_ms() {
                return Ok(());
            }
            if changed {
                state.error = None;
                state.retry_at = 0;
            }
            state.attempt_scope = Some(expected.clone());
            state.retry_at = now_ms() + 30_000;
            state.refreshing = true;
        }
        let _reset = Refreshing(&self.current);
        let result =
            tokio::time::timeout(Duration::from_secs(25), self.fetch(&settings, &expected)).await;
        let outcome = match result {
            Ok(result) => result,
            Err(_) => Err(Error::Timeout),
        };
        // The settings guard makes publication/admission atomic against Settings save's
        // cancellation. No network requests occur while it is held.
        let config = self.config.read().await;
        if scope(config.settings()) != expected {
            self.current
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .refreshing = false;
            return Ok(());
        }
        let predictions = match outcome {
            Ok(predictions) => predictions,
            Err(error) => {
                let mut state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
                state.error = Some(error.to_string());
                state.refreshing = false;
                state.retry_at = now_ms() + 30_000;
                drop(state);
                return Ok(());
            }
        };
        let saved = Saved {
            version: 1,
            scope: expected.clone(),
            updated_at: now_ms(),
            predictions,
        };
        let value = serde_json::to_string(&saved)?;
        if value.len() > MAX_BYTES {
            let mut state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            state.error = Some("Prediction snapshot exceeds storage limit".to_owned());
            state.retry_at = now_ms() + 30_000;
            drop(state);
            anyhow::bail!("prediction snapshot exceeds storage limit");
        }
        if let Err(error) = self.db.write_config(&[(STORE, &value)], &[]).await {
            let mut state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            state.refreshing = false;
            state.error = Some("Prediction results could not be saved".to_owned());
            state.retry_at = now_ms() + 30_000;
            drop(state);
            return Err(error.into());
        }
        self.current
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .warning = None;
        if !matches!(
            tokio::time::timeout(
                Duration::from_secs(5),
                self.admit_snapshot(&saved, &settings)
            )
            .await,
            Ok(Ok(true))
        ) {
            self.current
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .warning = Some(
                "Some predictions could not enter warming; the next refresh will retry admission"
                    .to_owned(),
            );
        }
        drop(config);
        let mut state = self.current.lock().unwrap_or_else(PoisonError::into_inner);
        state.saved = Some(saved);
        state.restored = false;
        state.refreshing = false;
        state.error = None;
        state.retry_at = now_ms() + i64::from(settings.prefetch.plex.refresh_seconds) * 1000;
        drop(state);
        Ok(())
    }
    async fn admit_snapshot(&self, saved: &Saved, settings: &Settings) -> anyhow::Result<bool> {
        let mut complete = true;
        if let Some(owner) = automatic_scope(settings) {
            let wanted = saved
                .predictions
                .iter()
                .filter(|p| p.item.is_some() && p.warning.is_none())
                .map(|p| p.id)
                .collect();
            self.queue
                .reconcile_automatic(Some(&owner), Some(&wanted))
                .await?;
            for p in &saved.predictions {
                if p.warning.is_none()
                    && let (Some(item), Some(revision)) = (p.item, p.revision.as_deref())
                {
                    // Metadata may change after the read-only pass. Admission rejects that
                    // candidate rather than silently warming its replacement revision.
                    if self
                        .queue
                        .enqueue_automatic(Automatic {
                            item: ItemId(item),
                            ranges: &p.ranges,
                            priority: if p.source == Source::Playback { 90 } else { 50 },
                            intent: p.id,
                            revision,
                            scope: &owner,
                        })
                        .await
                        .is_err()
                    {
                        complete = false;
                    }
                }
            }
        }
        Ok(complete)
    }
    async fn fetch(&self, settings: &Settings, scope: &str) -> Result<Vec<Prediction>, Error> {
        if !settings.prefetch.plex.enabled {
            return Ok(Vec::new());
        }
        let options = &settings.prefetch.plex;
        let mut predictions = Vec::new();
        let mut seen = HashSet::new();
        for server in settings.plex.servers.iter().filter(|s| s.enabled) {
            // Validate the configured machine before accepting history or sessions.
            self.client.test_server(server).await?;
            let mut inputs = Vec::new();
            if options.history {
                inputs.extend(
                    options
                        .policy
                        .history_candidates(
                            &self.client.recent_history(server).await?,
                            &server.id,
                            now_ms() / 1000,
                        )
                        .into_iter()
                        .map(|m| (m, false)),
                );
            }
            if options.realtime {
                for session in self
                    .client
                    .sessions(server)
                    .await?
                    .into_iter()
                    .filter(|s| matches!(s.state.as_str(), "playing" | "paused"))
                {
                    if options.policy.viewers.is_empty()
                        || options
                            .policy
                            .viewers
                            .contains(&format!("{}:{}", server.id, session.user))
                    {
                        let mut media = session.media;
                        media.user = Some(session.user);
                        inputs.push((media, true));
                    }
                }
            }
            if inputs.len() > 256 {
                return Err(Error::Invalid(
                    "Plex candidate pass exceeded its input limit",
                ));
            }
            let mut viewers = HashMap::<String, Option<ViewerServer>>::new();
            for (input, realtime) in inputs {
                if !options.policy.eligible(&input) {
                    continue;
                }
                let Some(user) = input.user.as_deref() else {
                    continue;
                };
                if !viewers.contains_key(user) {
                    let viewer = match self
                        .client
                        .viewer(server, user, &settings.plex.accounts)
                        .await
                    {
                        Ok(viewer) => Some(viewer),
                        Err(Error::Unauthorized) => None,
                        Err(error) => return Err(error),
                    };
                    viewers.insert(user.to_owned(), viewer);
                }
                let Some(viewer) = viewers.get(user).and_then(Option::as_ref) else {
                    if seen.insert((
                        server.id.clone(),
                        user.to_owned(),
                        input.rating_key.clone(),
                        Source::Playback as u8,
                    )) {
                        if predictions.len() >= MAX {
                            return Err(Error::Invalid(
                                "Plex prediction pass exceeded its result limit",
                            ));
                        }
                        let mut unknown = input.clone();
                        unknown.file = None;
                        let mut p = self
                            .prediction(server, &unknown, Source::Playback, scope, settings)
                            .await?;
                        p.item = None;
                        p.revision = None;
                        p.ranges.clear();
                        p.warning = Some(
                            "Viewer credentials could not be verified; connect that profile"
                                .to_owned(),
                        );
                        predictions.push(p);
                    }
                    continue;
                };
                let mut current = self
                    .client
                    .viewer_metadata(viewer, &input.rating_key)
                    .await?;
                // Session progress is real-time, while watched-state and paths come only
                // from metadata authorized for this exact viewer.
                if realtime {
                    current.view_offset_ms = input.view_offset_ms;
                    if input
                        .file
                        .as_ref()
                        .zip(current.file.as_ref())
                        .is_some_and(|(playing, scoped)| playing != scoped)
                    {
                        // Do not guess the selected version from metadata's first Part.
                        current.file = None;
                    }
                }
                let source = if realtime {
                    Source::Playback
                } else {
                    Source::PartialMovie
                };
                if (realtime || current.kind == "movie" && current.view_offset_ms > 0)
                    && current.view_offset_ms < current.duration_ms
                    && seen.insert((
                        server.id.clone(),
                        user.to_owned(),
                        current.rating_key.clone(),
                        source as u8,
                    ))
                {
                    predictions.push(
                        self.prediction(server, &current, source, scope, settings)
                            .await?,
                    );
                }
                if current.kind == "episode"
                    && let Some(show) = &current.show
                {
                    let next = self.client.next_episodes(viewer, show).await?;
                    for item in next_episodes(&current, &next, options.next_episodes as usize) {
                        if options.policy.eligible(&item)
                            && seen.insert((
                                server.id.clone(),
                                user.to_owned(),
                                item.rating_key.clone(),
                                Source::NextEpisode as u8,
                            ))
                        {
                            predictions.push(
                                self.prediction(
                                    server,
                                    &item,
                                    Source::NextEpisode,
                                    scope,
                                    settings,
                                )
                                .await?,
                            );
                        }
                    }
                }
                if predictions.len() > MAX {
                    return Err(Error::Invalid(
                        "Plex prediction pass exceeded its result limit",
                    ));
                }
            }
        }
        Ok(predictions)
    }
    async fn prediction(
        &self,
        server: &Server,
        media: &Media,
        source: Source,
        scope: &str,
        settings: &Settings,
    ) -> Result<Prediction, Error> {
        let mut p = Prediction {
            id: Uuid::nil(),
            server: server.id.clone(),
            viewer: media.user.clone().unwrap_or_default(),
            rating_key: media.rating_key.clone(),
            title: media.title.clone(),
            source,
            item: None,
            revision: None,
            size: 0,
            ranges: Vec::new(),
            warning: None,
        };
        let item = match media
            .file
            .as_deref()
            .and_then(|f| map_path(f, &server.path_mappings))
        {
            Some(MappedPath::Dav(path)) => match self.vfs.lookup(&path).await {
                Ok(Some(Node::Item(i) | Node::IdFile(i))) => Some(i),
                Ok(_) => None,
                Err(_) => return Err(Error::Invalid("Imported source metadata could not be read")),
            },
            Some(MappedPath::Local(_))
                if settings.prefetch.plex.local_files
                    == infinidysk_prefetch::LocalFiles::Disabled =>
            {
                None
            }
            Some(MappedPath::Local(path)) => {
                match (self.local)(path)
                    .await
                    .map_err(|()| Error::Invalid("Mapped local pointer could not be read"))?
                {
                    Some(id) => self.db.item(id).await.map_err(|_| {
                        Error::Invalid("Imported source catalogue could not be read")
                    })?,
                    None => None,
                }
            }
            None => None,
        };
        if let Some(item) = item.filter(|i| i.health_state != infinidysk_db::HealthState::Hidden) {
            let key = self
                .vfs
                .cache_key(&item)
                .await
                .map_err(|_| Error::Unavailable)?;
            p.size = key.size();
            p.item = Some(item.id.0);
            p.revision = Some(key.revision().to_owned());
            p.ranges = settings
                .prefetch
                .plex
                .policy
                .ranges(
                    p.size,
                    if source == Source::NextEpisode {
                        0
                    } else {
                        media.view_offset_ms
                    },
                    media.duration_ms,
                    false,
                )
                .into_iter()
                .map(|(start, len)| Range {
                    start,
                    end: start + len,
                })
                .collect();
        }
        if p.item.is_none() || p.ranges.is_empty() {
            p.warning =
                Some("No exact imported source and path mapping could be resolved".to_owned());
        }
        // No tokens, titles or paths in public job identities. Scope contains only a digest.
        let identity = serde_json::to_vec(&(
            scope,
            &p.server,
            &p.viewer,
            &p.rating_key,
            p.source,
            p.item,
            &p.revision,
            &p.ranges,
        ))
        .expect("serializable fields");
        let hash = Sha256::digest(identity);
        let mut bytes = [0; 16];
        bytes.copy_from_slice(&hash[..16]);
        p.id = Uuid::from_bytes(bytes);
        Ok(p)
    }
}
fn valid_saved(saved: &Saved) -> bool {
    saved.version == 1
        && saved.scope.len() == 64
        && saved.updated_at > 0
        && saved.predictions.len() <= MAX
        && saved.predictions.iter().all(|p| {
            p.ranges.len() <= 64 && p.ranges.iter().all(|r| r.start < r.end && r.end <= p.size)
        })
}
pub(crate) fn scope(settings: &Settings) -> String {
    let bytes = serde_json::to_vec(&(
        &settings.plex,
        &settings.prefetch.plex,
        settings.prefetch.enabled,
        &settings.mount,
        &settings.imports,
    ))
    .expect("serializable settings");
    Sha256::digest(bytes)
        .iter()
        .fold(String::with_capacity(64), |mut s, b| {
            write!(s, "{b:02x}").expect("string formatting");
            s
        })
}
pub(crate) fn automatic_scope(settings: &Settings) -> Option<String> {
    (settings.prefetch.enabled && settings.prefetch.plex.enabled).then(|| scope(settings))
}
#[utoipa::path(get,operation_id="plex_predictions",path="/api/v1/prefetch/predictions",responses((status=200,body=Snapshot)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Json<Snapshot> {
    Json(admin.predictions.snapshot().await)
}
#[utoipa::path(post,operation_id="plex_predictions_refresh",path="/api/v1/prefetch/predictions",responses((status=200,body=Snapshot)))]
pub async fn refresh(State(admin): State<Arc<Admin>>) -> Result<Json<Snapshot>, Fail> {
    // Spawned work belongs to the application, not to a disconnected browser request.
    let service = Arc::clone(&admin.predictions);
    let cancel = admin.cancel.clone();
    tokio::spawn(async move {
        tokio::select! {()=cancel.cancelled()=>{},result=service.tick(true)=>if let Err(error)=result {tracing::warn!(reason=%error,"Plex prediction refresh deferred");}}
    });
    Ok(Json(admin.predictions.snapshot().await))
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
mod tests {
    use super::*;
    use axum::{
        Router,
        body::Body,
        http::{HeaderMap, Response, Uri},
    };
    use infinidysk_core::{BlobId, FileMeta, ItemKind, NzbFile, Segment, Segments, blob};
    use infinidysk_db::Item;
    use infinidysk_native_cache::{BLOCK, Cache, FolderSettings, Settings as CacheSettings};
    use infinidysk_nntp::{
        Client as Nntp, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode,
    };
    use infinidysk_stream::Budget;
    use infinidysk_testkit::{Corpus, LoopbackServer};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use tokio::sync::Notify;
    struct Temp(PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    #[derive(Default)]
    struct Plex {
        mode: AtomicUsize,
        calls: AtomicUsize,
        scoped: AtomicUsize,
        entered: Notify,
        release: Notify,
    }
    impl Plex {
        async fn handle(self: Arc<Self>, uri: Uri, headers: HeaderMap) -> Response<Body> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            assert!(
                !uri.query()
                    .unwrap_or_default()
                    .to_ascii_lowercase()
                    .contains("token")
            );
            let mode = self.mode.load(Ordering::SeqCst);
            let path = uri.path();
            let xml = |body: String| {
                Response::builder()
                    .header("content-type", "application/xml")
                    .body(Body::from(body))
                    .unwrap()
            };
            let json = |body: serde_json::Value| {
                Response::builder()
                    .header("content-type", "application/json")
                    .body(Body::from(body.to_string()))
                    .unwrap()
            };
            if path == "/api/v2/user" {
                if mode == 2 {
                    return Response::builder().status(401).body(Body::empty()).unwrap();
                }
                return json(
                    serde_json::json!({"uuid":"profile","id":7,"username":"Fixture profile"}),
                );
            }
            if path == "/api/v2/resources" {
                return json(
                    serde_json::json!([{"provides":"server","clientIdentifier":"machine","name":"Fixture","accessToken":"fixture-viewer-resource","owned":false,"connections":[]}]),
                );
            }
            if path == "/identity" {
                return xml("<MediaContainer machineIdentifier=\"machine\"/>".into());
            }
            if path == "/library/sections" {
                return xml("<MediaContainer/>".into());
            }
            if path == "/status/sessions/history/all" {
                if mode == 1 {
                    return Response::builder().status(503).body(Body::empty()).unwrap();
                }
                return xml(format!(
                    "<MediaContainer totalSize=\"1\" offset=\"0\">{} </MediaContainer>",
                    video("current", 1, "/media/current.mkv", Some(now_ms() / 1000), 0)
                ));
            }
            if path == "/status/sessions" {
                return xml(format!("<MediaContainer>{}</MediaContainer>",video("current",1,if mode==5 {"/media/other.mkv"}else{"/media/current.mkv"},None,0).replace("</Video>","<User id=\"7\" title=\"Fixture profile\"/><Player state=\"playing\" machineIdentifier=\"player\"/><Session id=\"session\"/></Video>")));
            }
            if path.starts_with("/library/metadata/") {
                assert_eq!(
                    headers.get("x-plex-token").unwrap(),
                    "fixture-viewer-resource",
                    "owner watched state must never be substituted"
                );
                self.scoped.fetch_add(1, Ordering::SeqCst);
                if mode == 4 && path.ends_with("/current") {
                    self.mode.store(0, Ordering::SeqCst);
                    self.entered.notify_one();
                    self.release.notified().await;
                }
                if path.ends_with("/allLeaves") {
                    if mode == 3 {
                        return Response::builder().status(503).body(Body::empty()).unwrap();
                    }
                    return xml(format!(
                        "<MediaContainer totalSize=\"2\" offset=\"0\">{}{}</MediaContainer>",
                        video("watched", 2, "/media/next.mkv", None, 1),
                        video("next", 3, "/media/next.mkv", None, 0)
                    ));
                }
                return xml(format!(
                    "<MediaContainer>{}</MediaContainer>",
                    video("current", 1, "/media/current.mkv", None, 0)
                ));
            }
            panic!("unexpected fixture route {path}");
        }
    }
    fn video(key: &str, episode: u32, file: &str, viewed: Option<i64>, watched: u32) -> String {
        format!(
            "<Video ratingKey=\"{key}\" type=\"episode\" title=\"Fixture episode\" grandparentRatingKey=\"show\" parentIndex=\"1\" index=\"{episode}\" accountID=\"7\" viewCount=\"{watched}\" viewOffset=\"1000\" duration=\"60000\" {}><Media><Part file=\"{file}\"/></Media></Video>",
            viewed.map_or_else(String::new, |v| format!("viewedAt=\"{v}\""))
        )
    }
    struct Harness {
        service: Arc<Predictions>,
        state: Arc<Plex>,
        task: tokio::task::JoinHandle<()>,
        queue: Arc<Queue>,
        config: Arc<RwLock<ConfigStore>>,
        db: Db,
        vfs: Arc<Vfs>,
        files: Vec<Item>,
        server: LoopbackServer,
        _temp: Temp,
    }
    impl Drop for Harness {
        fn drop(&mut self) {
            self.task.abort();
        }
    }
    async fn fixture() -> Harness {
        let path = std::env::temp_dir().join(format!("infinidysk-predictions-{}", Uuid::new_v4()));
        std::fs::create_dir_all(path.join("payload")).unwrap();
        let temp = Temp(path);
        let db = Db::connect(&format!("sqlite:{}", temp.0.join("db.sqlite").display()))
            .await
            .unwrap();
        let corpus = Corpus::generate(1025, 16, 256 * 1024);
        let server = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
        let category = Item::child(&root, "fixture", ItemKind::Directory).unwrap();
        db.insert(std::slice::from_ref(&category), &[])
            .await
            .unwrap();
        let mut files = Vec::new();
        for name in ["current.mkv", "next.mkv"] {
            let mut item = Item::child(&category, name, ItemKind::NzbFile).unwrap();
            let id = BlobId::new();
            item.meta_blob = Some(id);
            item.size = Some(BLOCK);
            let meta = FileMeta::Nzb(NzbFile {
                segments: Segments {
                    items: corpus
                        .articles
                        .iter()
                        .map(|a| Segment {
                            id: a.message_id.clone(),
                            size: 256 * 1024,
                            fallbacks: vec![],
                        })
                        .collect(),
                    sizes_trusted: true,
                    ..Default::default()
                },
                proof: None,
            });
            db.insert(
                std::slice::from_ref(&item),
                &[(id, &blob::encode_meta(&meta).unwrap())],
            )
            .await
            .unwrap();
            files.push(item);
        }
        let pool = Pool::new(
            Endpoint::new(
                "127.0.0.1",
                server.addr().port(),
                TlsMode::Plain,
                None,
                Duration::from_secs(3),
            )
            .unwrap(),
            PoolConfig {
                max_connections: 5,
                pipeline_depth: 4,
                min_connections: 0,
                idle_timeout: Duration::from_secs(5),
            },
            Downloads::new(16),
        )
        .unwrap();
        let client = Arc::new(Nntp::new([Provider {
            pool,
            key: "fixture".into(),
            name: "fixture".into(),
            priority: 0,
            backbone: None,
        }]));
        let cache = Cache::open(CacheSettings {
            enabled: true,
            min_file_mb: 0,
            metadata_path: temp.0.join("meta").to_string_lossy().into_owned(),
            folders: vec![FolderSettings {
                id: Uuid::new_v4(),
                path: temp.0.join("payload").to_string_lossy().into_owned(),
                min_free_bytes: 0,
                ..Default::default()
            }],
            ..Default::default()
        })
        .await
        .unwrap();
        let vfs = Arc::new(
            Vfs::new(
                db.clone(),
                client,
                Budget::new(8 * 1024 * 1024),
                0,
                "/unused",
            )
            .with_native_cache(Some(cache)),
        );
        let queue = Queue::load(db.clone(), Arc::clone(&vfs)).await.unwrap();
        let state = Arc::new(Plex::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let router = Router::new()
            .fallback(
                |State(s): State<Arc<Plex>>, uri: Uri, headers: HeaderMap| async move {
                    s.handle(uri, headers).await
                },
            )
            .with_state(Arc::clone(&state));
        let task = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        let mut config = ConfigStore::load(db.clone(), std::iter::empty())
            .await
            .unwrap();
        let mut settings = config.settings().clone();
        settings.native_cache.enabled = true;
        settings.native_cache.folders = vec![FolderSettings {
            id: Uuid::new_v4(),
            path: temp.0.join("payload").to_string_lossy().into_owned(),
            min_free_bytes: 0,
            ..Default::default()
        }];
        settings.native_cache.metadata_path = temp.0.join("meta").to_string_lossy().into_owned();
        settings.prefetch.enabled = true;
        settings.prefetch.plex.enabled = true;
        settings.prefetch.plex.history = true;
        settings.prefetch.plex.policy.min_distinct_episodes = 1;
        settings.prefetch.plex.policy.confidence_percent = 0;
        settings.plex.accounts = vec![infinidysk_plex::Account {
            id: "profile".into(),
            numeric_id: Some("7".into()),
            name: "Fixture profile".into(),
            token: infinidysk_plex::Token("fixture-cloud-token".into()),
        }];
        settings.plex.servers = vec![Server {
            id: "machine".into(),
            name: "Fixture".into(),
            url: format!("http://127.0.0.1:{port}"),
            token: infinidysk_plex::Token("fixture-owner-token".into()),
            enabled: true,
            account_id: None,
            path_mappings: vec![infinidysk_plex::PathMapping {
                plex_path: "/media".into(),
                dav_path: Some(category.path.clone()),
                local_path: None,
            }],
        }];
        config.save(settings).await.unwrap();
        let config = Arc::new(RwLock::new(config));
        let service = Predictions::load(
            db.clone(),
            Client::loopback(Uuid::new_v4(), port).unwrap(),
            Arc::clone(&config),
            Arc::clone(&vfs),
            Arc::clone(&queue),
            Arc::new(|_| Box::pin(async { Ok(None) })),
        )
        .await
        .unwrap();
        Harness {
            service,
            state,
            task,
            queue,
            config,
            db,
            vfs,
            files,
            server,
            _temp: temp,
        }
    }
    #[tokio::test]
    async fn verified_predictions_admit_and_warm_once_using_viewer_state() {
        let f = fixture().await;
        f.service.tick(true).await.unwrap();
        let snapshot = f.service.snapshot().await;
        assert!(!snapshot.stale);
        assert_eq!(snapshot.predictions.len(), 2);
        assert!(
            snapshot
                .predictions
                .iter()
                .all(|p| p.warning.is_none() && p.viewer == "7")
        );
        assert!(snapshot.predictions.iter().any(|p| p.rating_key == "next"));
        assert!(
            !snapshot
                .predictions
                .iter()
                .any(|p| p.rating_key == "watched")
        );
        assert!(f.state.scoped.load(Ordering::SeqCst) > 0);
        assert_eq!(f.queue.status().await.jobs.len(), 2);
        let options = f.config.read().await.settings().prefetch.clone();
        assert!(f.queue.tick(&options).await.unwrap());
        assert!(f.queue.tick(&options).await.unwrap());
        let before = f.server.body_commands();
        assert_eq!(before, 32);
        f.service.tick(true).await.unwrap();
        assert_eq!(f.queue.status().await.jobs.len(), 2);
        assert!(!f.queue.tick(&options).await.unwrap());
        assert_eq!(f.server.body_commands(), before);
        let json = serde_json::to_string(&f.service.snapshot().await).unwrap();
        assert!(!json.contains("fixture-cloud-token") && !json.contains("fixture-viewer-resource"));
    }
    #[tokio::test]
    async fn incomplete_pass_retains_last_complete_snapshot_and_backoff() {
        let f = fixture().await;
        f.service.tick(true).await.unwrap();
        let first = f.service.snapshot().await;
        f.state.mode.store(3, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        let failed = f.service.snapshot().await;
        assert!(failed.stale && failed.error.is_some());
        assert_eq!(failed.updated_at, first.updated_at);
        assert_eq!(
            failed.predictions.iter().map(|p| p.id).collect::<Vec<_>>(),
            first.predictions.iter().map(|p| p.id).collect::<Vec<_>>()
        );
        let calls = f.state.calls.load(Ordering::SeqCst);
        f.service.tick(false).await.unwrap();
        assert_eq!(f.state.calls.load(Ordering::SeqCst), calls);
        assert_eq!(f.queue.status().await.jobs.len(), 2);
        let restored = Predictions::load(
            f.db.clone(),
            f.service.client.clone(),
            Arc::clone(&f.config),
            Arc::clone(&f.vfs),
            Arc::clone(&f.queue),
            Arc::new(|_| Box::pin(async { Ok(None) })),
        )
        .await
        .unwrap();
        let restored = restored.snapshot().await;
        assert!(restored.stale);
        assert_eq!(restored.updated_at, first.updated_at);
    }
    #[tokio::test]
    async fn unverified_viewer_is_ineligible_and_never_uses_owner_metadata() {
        let f = fixture().await;
        f.state.mode.store(2, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        let snapshot = f.service.snapshot().await;
        assert!(!snapshot.predictions.is_empty());
        assert!(
            snapshot
                .predictions
                .iter()
                .all(|p| p.item.is_none() && p.warning.is_some())
        );
        assert_eq!(f.state.scoped.load(Ordering::SeqCst), 0);
        assert!(f.queue.status().await.jobs.is_empty());
    }
    #[tokio::test]
    async fn policy_change_discards_inflight_result_and_cancels_obsolete_jobs() {
        let f = fixture().await;
        f.service.tick(true).await.unwrap();
        let manual = f
            .queue
            .enqueue(
                f.files[0].id,
                &[Range {
                    start: 0,
                    end: BLOCK,
                }],
                50,
                Uuid::new_v4(),
            )
            .await
            .unwrap();
        f.state.mode.store(4, Ordering::SeqCst);
        let running = {
            let s = Arc::clone(&f.service);
            tokio::spawn(async move { s.tick(true).await })
        };
        f.state.entered.notified().await;
        {
            let mut config = f.config.write().await;
            let mut settings = config.settings().clone();
            settings.prefetch.plex.enabled = false;
            config.save(settings).await.unwrap();
            drop(config);
        }
        f.state.release.notify_one();
        running.await.unwrap().unwrap();
        assert!(f.service.snapshot().await.predictions.is_empty());
        f.service.tick(false).await.unwrap();
        let jobs = f.queue.status().await.jobs;
        assert!(
            jobs.iter()
                .filter(|j| j.id != manual.id)
                .all(|j| j.state == infinidysk_prefetch::State::Cancelled)
        );
        assert_eq!(
            jobs.iter().find(|j| j.id == manual.id).unwrap().state,
            infinidysk_prefetch::State::Queued
        );
        assert!(!f.service.snapshot().await.refreshing);
    }
    #[tokio::test]
    async fn source_revision_change_cannot_admit_the_stale_prediction() {
        let f = fixture().await;
        f.service.tick(true).await.unwrap();
        let p = f
            .service
            .snapshot()
            .await
            .predictions
            .into_iter()
            .find(|p| p.item == Some(f.files[0].id.0))
            .unwrap();
        let item = &f.files[0];
        let mut meta =
            blob::decode_meta(&f.db.blob(item.meta_blob.unwrap()).await.unwrap().unwrap()).unwrap();
        if let FileMeta::Nzb(n) = &mut meta {
            n.segments.items[0].id = "replacement@fixture.invalid".into();
        }
        f.db.update_blob(item.meta_blob.unwrap(), &blob::encode_meta(&meta).unwrap())
            .await
            .unwrap();
        assert!(
            f.queue
                .enqueue_automatic(Automatic {
                    item: item.id,
                    ranges: &p.ranges,
                    priority: 50,
                    intent: Uuid::new_v4(),
                    revision: p.revision.as_deref().unwrap(),
                    scope: &scope(f.config.read().await.settings())
                })
                .await
                .is_err()
        );
    }
    #[tokio::test]
    async fn local_pointer_mapping_is_opt_in_and_io_uncertainty_preserves_the_snapshot() {
        let f = fixture().await;
        {
            let mut config = f.config.write().await;
            let mut settings = config.settings().clone();
            let mapping = &mut settings.plex.servers[0].path_mappings[0];
            mapping.dav_path = None;
            mapping.local_path = Some("/local".into());
            config.save(settings).await.unwrap();
            drop(config);
        }
        let calls = Arc::new(AtomicUsize::new(0));
        let called = Arc::clone(&calls);
        let ids = [f.files[0].id, f.files[1].id];
        let local: LocalResolver = Arc::new(move |path| {
            called.fetch_add(1, Ordering::SeqCst);
            let id = if path.ends_with("current.mkv") {
                ids[0]
            } else {
                ids[1]
            };
            Box::pin(async move { Ok(Some(id)) })
        });
        let service = Predictions::load(
            f.db.clone(),
            f.service.client.clone(),
            Arc::clone(&f.config),
            Arc::clone(&f.vfs),
            Arc::clone(&f.queue),
            local,
        )
        .await
        .unwrap();
        service.tick(true).await.unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        assert!(
            service
                .snapshot()
                .await
                .predictions
                .iter()
                .all(|p| p.warning.is_some())
        );
        {
            let mut config = f.config.write().await;
            let mut settings = config.settings().clone();
            settings.prefetch.plex.local_files = infinidysk_prefetch::LocalFiles::ImportedPointers;
            config.save(settings).await.unwrap();
            drop(config);
        }
        service.tick(true).await.unwrap();
        let saved = service.snapshot().await;
        assert!(saved.predictions.iter().all(|p| p.warning.is_none()));
        assert_eq!(f.queue.status().await.jobs.len(), 2);
        let unavailable = Predictions::load(
            f.db.clone(),
            f.service.client.clone(),
            Arc::clone(&f.config),
            Arc::clone(&f.vfs),
            Arc::clone(&f.queue),
            Arc::new(|_| Box::pin(async { Err(()) })),
        )
        .await
        .unwrap();
        unavailable.tick(true).await.unwrap();
        let snapshot = unavailable.snapshot().await;
        assert!(snapshot.stale && snapshot.error.is_some());
        assert_eq!(snapshot.updated_at, saved.updated_at);
    }
    #[tokio::test]
    async fn invalid_saved_format_is_reported_and_a_fresh_pass_recovers_it() {
        let f = fixture().await;
        f.db.write_config(&[(STORE, "{}")], &[]).await.unwrap();
        let service = Predictions::load(
            f.db.clone(),
            f.service.client.clone(),
            Arc::clone(&f.config),
            Arc::clone(&f.vfs),
            Arc::clone(&f.queue),
            Arc::new(|_| Box::pin(async { Ok(None) })),
        )
        .await
        .unwrap();
        assert!(service.snapshot().await.warning.is_some());
        service.tick(true).await.unwrap();
        assert!(service.snapshot().await.warning.is_none());
    }
    #[tokio::test]
    async fn automatic_cancel_is_respected_and_eviction_requeues_completed_intent() {
        let f = fixture().await;
        f.service.tick(true).await.unwrap();
        let jobs = f.queue.status().await.jobs;
        let first = jobs[0].clone();
        f.queue
            .control(first.id, infinidysk_prefetch::Operation::Cancel)
            .await
            .unwrap();
        f.service.tick(true).await.unwrap();
        assert_eq!(f.queue.status().await.jobs.len(), 2);
        assert_eq!(
            f.queue
                .status()
                .await
                .jobs
                .iter()
                .find(|j| j.id == first.id)
                .unwrap()
                .state,
            infinidysk_prefetch::State::Cancelled
        );
        let options = f.config.read().await.settings().prefetch.clone();
        assert!(f.queue.tick(&options).await.unwrap());
        let completed = f
            .queue
            .status()
            .await
            .jobs
            .into_iter()
            .find(|j| j.state == infinidysk_prefetch::State::Complete)
            .unwrap();
        let item = f.db.item(ItemId(completed.item)).await.unwrap().unwrap();
        let key = f.vfs.cache_key(&item).await.unwrap();
        assert!(
            f.vfs
                .native_cache()
                .unwrap()
                .evict(&key, "fixture")
                .await
                .unwrap()
        );
        f.service.tick(true).await.unwrap();
        assert_eq!(
            f.queue
                .status()
                .await
                .jobs
                .iter()
                .find(|j| j.id == completed.id)
                .unwrap()
                .state,
            infinidysk_prefetch::State::Queued
        );
        assert_eq!(f.queue.status().await.jobs.len(), 2);
    }
    #[tokio::test]
    async fn automatic_warming_and_playback_share_verified_cache_without_nntp_reads() {
        use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _};
        let f = fixture().await;
        let expected = Corpus::generate(1025, 16, 256 * 1024).data;
        for item in &f.files {
            let key = f.vfs.cache_key(item).await.unwrap();
            let cache = f.vfs.native_cache().unwrap();
            let lease = cache.lease(key).unwrap();
            assert!(cache.put(&lease, 0, expected.clone().into()).await.unwrap());
        }
        f.service.tick(true).await.unwrap();
        let options = f.config.read().await.settings().prefetch.clone();
        assert!(f.queue.tick(&options).await.unwrap());
        assert!(f.queue.tick(&options).await.unwrap());
        assert_eq!(f.server.body_commands(), 0);
        assert!(
            f.queue
                .status()
                .await
                .jobs
                .iter()
                .all(|j| j.source_bytes == 0 && j.cached_bytes == BLOCK)
        );
        let mut reader = f
            .vfs
            .open(
                &Node::Item(f.files[0].clone()),
                infinidysk_vfs::Origin {
                    delivery: infinidysk_vfs::Delivery::WebDav,
                    client: "fixture".into(),
                    player: None,
                },
            )
            .await
            .unwrap()
            .unwrap();
        reader
            .seek(std::io::SeekFrom::Start(BLOCK - 128))
            .await
            .unwrap();
        let mut b = [0; 64];
        reader.read_exact(&mut b).await.unwrap();
        assert_eq!(b, expected[expected.len() - 128..expected.len() - 64]);
        assert_eq!(f.server.body_commands(), 0);
    }
    #[tokio::test]
    async fn mismatched_playing_media_version_is_not_guessed_from_metadata() {
        let f = fixture().await;
        f.state.mode.store(5, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        let snapshot = f.service.snapshot().await;
        assert!(
            snapshot
                .predictions
                .iter()
                .any(|p| p.source == Source::Playback && p.item.is_none() && p.warning.is_some())
        );
        assert_eq!(f.queue.status().await.jobs.len(), 1);
    }
}
