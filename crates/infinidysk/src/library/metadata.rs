//! Complete Plex metadata snapshots. Classification is read-only and never establishes
//! source ownership, viewer authorization, or permission to warm/delete a file.
use crate::{
    admin::{Admin, Fail},
    config::{ConfigStore, Settings},
};
use axum::{Json, extract::State};
use infinidysk_db::{Db, Item, now_ms};
use infinidysk_plex::{Client, MappedPath, Server, map_path};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    fmt::Write as _,
    sync::{Arc, Mutex, OnceLock, PoisonError},
    time::Duration,
};
use tokio::sync::{Mutex as AsyncMutex, RwLock, Semaphore};
use utoipa::ToSchema;

const DAY: i64 = 86_400_000;
const MAX_ITEMS: usize = 100_000;
const MAX_MATCHES: usize = 100;
const MAX_BYTES: usize = 64 * 1024 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(crate) struct Entry {
    pub server: String,
    pub library: String,
    pub key: String,
    pub kind: String,
    pub title: String,
    pub show: Option<String>,
    pub show_title: Option<String>,
    pub season: Option<u32>,
    pub episode: Option<u32>,
    pub year: Option<u32>,
    pub files: Vec<String>,
}
#[derive(Serialize, Deserialize)]
struct Saved {
    version: u8,
    scope: String,
    at: i64,
    entries: Vec<Entry>,
}
struct Index {
    saved: Saved,
    by_path: HashMap<(String, String), Vec<usize>>,
    by_name: HashMap<String, Vec<usize>>,
}
impl Index {
    fn new(saved: Saved) -> Self {
        let mut by_path: HashMap<_, Vec<_>> = HashMap::new();
        let mut by_name: HashMap<_, Vec<_>> = HashMap::new();
        for (i, e) in saved.entries.iter().enumerate() {
            let mut names = HashSet::new();
            for path in &e.files {
                by_path
                    .entry((e.server.clone(), normalized(path)))
                    .or_default()
                    .push(i);
                if let Some(name) = filename(path)
                    && names.insert(name.clone())
                {
                    by_name.entry(name).or_default().push(i);
                }
            }
        }
        Self {
            saved,
            by_path,
            by_name,
        }
    }
}
#[derive(Default)]
struct Current {
    index: Option<Arc<Index>>,
    error: Option<String>,
    refreshing: bool,
    restored: bool,
    retry_at: i64,
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
pub struct Metadata {
    db: Db,
    config: Arc<RwLock<ConfigStore>>,
    client: Client,
    current: Mutex<Current>,
    gate: AsyncMutex<()>,
    index_slots: Arc<Semaphore>,
    busy: OnceLock<Arc<dyn Fn() -> bool + Send + Sync>>,
}

fn scope(s: &Settings) -> String {
    // Credentials affect scope but only the digest is persisted.
    let data = serde_json::to_vec(&(&s.plex, s.imports.plex_matching, &s.imports.plex_server_ids))
        .expect("Plex scope contains only serializable strings, lists and booleans");
    Sha256::digest(data)
        .iter()
        .fold(String::with_capacity(64), |mut out, b| {
            write!(out, "{b:02x}").expect("formatting into String cannot fail");
            out
        })
}
fn selected(s: &Settings) -> impl Iterator<Item = &Server> {
    s.plex.servers.iter().filter(|server| {
        server.enabled
            && s.imports
                .plex_server_ids
                .as_ref()
                .is_none_or(|ids| ids.contains(&server.id))
    })
}
fn normalized(path: &str) -> String {
    let path = path.replace('\\', "/");
    if path.as_bytes().get(1) == Some(&b':') {
        path.to_ascii_lowercase()
    } else {
        path
    }
}
fn filename(path: &str) -> Option<String> {
    let normalized = path.replace('\\', "/");
    let name = normalized.rsplit('/').next()?;
    (!name.is_empty()).then(|| name.to_lowercase())
}
fn valid(saved: &Saved) -> bool {
    saved.version == 1
        && saved.at >= 0
        && saved.entries.len() <= MAX_ITEMS
        && saved.scope.len() == 64
        && saved.entries.iter().all(|e| {
            !e.server.is_empty()
                && !e.key.is_empty()
                && e.title.len() <= 4096
                && e.show_title.as_ref().is_none_or(|s| s.len() <= 4096)
                && e.files.len() <= 64
                && e.files
                    .iter()
                    .all(|p| p.len() <= 4096 && !p.chars().any(char::is_control))
        })
}
impl Metadata {
    pub async fn load(
        db: Db,
        config: Arc<RwLock<ConfigStore>>,
        client: Client,
    ) -> anyhow::Result<Arc<Self>> {
        let raw = db.plex_classification().await?;
        let mut current = Current::default();
        if let Some(raw) = raw {
            let loaded = tokio::task::spawn_blocking(move || {
                if raw.len() > MAX_BYTES {
                    return None;
                }
                let saved: Saved = serde_json::from_str(&raw).ok()?;
                valid(&saved).then(|| Arc::new(Index::new(saved)))
            })
            .await?;
            current.error = loaded
                .is_none()
                .then(|| "Saved Plex classification could not be read; sync again".to_owned());
            current.restored = loaded.is_some();
            current.index = loaded;
        }
        Ok(Arc::new(Self {
            db,
            config,
            client,
            current: Mutex::new(current),
            gate: AsyncMutex::new(()),
            index_slots: Arc::new(Semaphore::new(1)),
            busy: OnceLock::new(),
        }))
    }
    pub(crate) fn set_busy(&self, busy: Arc<dyn Fn() -> bool + Send + Sync>) {
        let _ = self.busy.set(busy);
    }
    fn is_busy(&self) -> bool {
        self.busy.get().is_some_and(|busy| busy())
    }
    pub async fn run(self: Arc<Self>) {
        loop {
            let _ = self.tick(false).await;
            tokio::time::sleep(Duration::from_secs(30)).await;
        }
    }
    pub async fn status(&self) -> Status {
        let settings = self.config.read().await.settings().clone();
        let expected = scope(&settings);
        let current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
        let index = current
            .index
            .as_ref()
            .filter(|i| i.saved.scope == expected && settings.imports.plex_matching);
        Status {
            enabled: settings.imports.plex_matching,
            servers: u32::try_from(selected(&settings).count()).unwrap_or(u32::MAX),
            stale: current.restored
                || current.error.is_some()
                || index.is_none_or(|i| now_ms().saturating_sub(i.saved.at) >= DAY),
            refreshing: current.refreshing,
            updated_at: index.map(|i| i.saved.at),
            entries: index.map_or(0, |i| {
                u32::try_from(i.saved.entries.len()).unwrap_or(u32::MAX)
            }),
            error: current.error.clone(),
        }
    }
    pub async fn tick(&self, force: bool) -> anyhow::Result<()> {
        let Ok(_gate) = self.gate.try_lock() else {
            return Ok(());
        };
        let settings = self.config.read().await.settings().clone();
        if !settings.imports.plex_matching || (!force && self.is_busy()) {
            return Ok(());
        }
        let expected = scope(&settings);
        {
            let mut current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            let fresh = current.index.as_ref().is_some_and(|i| {
                i.saved.scope == expected && now_ms().saturating_sub(i.saved.at) < DAY
            });
            if !force
                && (now_ms() < current.retry_at
                    || (fresh && !current.restored && current.error.is_none()))
            {
                return Ok(());
            }
            current.refreshing = true;
        }
        let _refreshing = Refreshing(&self.current);
        let fetched =
            tokio::time::timeout(Duration::from_secs(25), self.fetch(&settings, force)).await;
        let Ok(Ok(entries)) = fetched else {
            let mut current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            current.error = Some(
                "Plex classification sync incomplete; previous complete snapshot retained".into(),
            );
            current.retry_at = now_ms() + 60_000;
            drop(current);
            return Ok(());
        };
        if !force && self.is_busy() {
            return Ok(());
        }
        let result = self.publish(entries, &expected, force).await;
        if result.is_err() {
            let mut current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            current.error =
                Some("Plex classification could not be saved; previous snapshot retained".into());
            current.retry_at = now_ms() + 60_000;
        }
        result
    }
    async fn publish(
        &self,
        entries: Vec<Entry>,
        expected: &str,
        force: bool,
    ) -> anyhow::Result<()> {
        let saved = Saved {
            version: 1,
            scope: expected.to_owned(),
            at: now_ms(),
            entries,
        };
        let permit = Arc::clone(&self.index_slots).try_acquire_owned()?;
        // An abandoned HTTP request cannot release this budget while its blocking
        // index build is still running. No unbounded pile-up of large snapshots.
        let (raw, index) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            let raw = serde_json::to_string(&saved)?;
            if raw.len() > MAX_BYTES {
                anyhow::bail!("Plex classification snapshot exceeds memory bound");
            }
            Ok::<_, anyhow::Error>((raw, Arc::new(Index::new(saved))))
        })
        .await??;
        let config = self.config.read().await;
        if !force && self.is_busy() {
            anyhow::bail!("Plex classification yielded to foreground work");
        }
        if scope(config.settings()) != expected {
            anyhow::bail!("Plex classification configuration changed during sync");
        }
        self.db.save_plex_classification(&raw).await?;
        let mut current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
        current.index = Some(index);
        current.restored = false;
        current.error = None;
        current.retry_at = 0;
        drop(current);
        drop(config);
        Ok(())
    }
    async fn fetch(&self, s: &Settings, force: bool) -> Result<Vec<Entry>, infinidysk_plex::Error> {
        let mut entries = Vec::new();
        let mut bytes = 0;
        let mut seen = HashSet::new();
        let mut servers = 0;
        for server in selected(s) {
            if !force && self.is_busy() {
                return Err(infinidysk_plex::Error::Invalid(
                    "Classification yielded to foreground activity",
                ));
            }
            servers += 1;
            self.client.test_server(server).await?;
            let libraries = self.client.libraries(server).await?;
            if libraries.is_empty() {
                return Err(infinidysk_plex::Error::Invalid(
                    "No accessible video libraries",
                ));
            }
            for library in libraries {
                if !force && self.is_busy() {
                    return Err(infinidysk_plex::Error::Invalid(
                        "Classification yielded to foreground activity",
                    ));
                }
                for row in self.client.catalogue(server, &library).await? {
                    if !seen.insert((server.id.clone(), row.media.rating_key.clone())) {
                        return Err(infinidysk_plex::Error::Invalid(
                            "Ambiguous Plex classification identity",
                        ));
                    }
                    let entry = Entry {
                        server: server.id.clone(),
                        library: library.id.clone(),
                        key: row.media.rating_key,
                        kind: row.media.kind,
                        title: row.media.title,
                        show: row.media.show,
                        show_title: row.media.show_title,
                        season: row.media.season,
                        episode: row.media.episode,
                        year: row.year,
                        files: row.files,
                    };
                    if entry.title.len() > 4096
                        || entry.show_title.as_ref().is_some_and(|s| s.len() > 4096)
                    {
                        return Err(infinidysk_plex::Error::Invalid(
                            "Plex classification text exceeds bound",
                        ));
                    }
                    bytes += serde_json::to_vec(&entry)
                        .map_err(|_| {
                            infinidysk_plex::Error::Invalid(
                                "Plex classification could not be encoded",
                            )
                        })?
                        .len();
                    if entries.len() >= MAX_ITEMS || bytes > MAX_BYTES {
                        return Err(infinidysk_plex::Error::Invalid(
                            "Plex classification exceeds snapshot bound",
                        ));
                    }
                    entries.push(entry);
                    if entries.len() % 1000 == 0 {
                        tokio::task::yield_now().await;
                        if !force && self.is_busy() {
                            return Err(infinidysk_plex::Error::Invalid(
                                "Classification yielded to foreground activity",
                            ));
                        }
                    }
                }
            }
        }
        if servers == 0 {
            return Err(infinidysk_plex::Error::Invalid(
                "Select an enabled Plex server",
            ));
        }
        Ok(entries)
    }
    pub async fn match_file(
        &self,
        item: &Item,
        links: &[super::catalogue::FileView],
    ) -> Classification {
        let settings = self.config.read().await.settings().clone();
        if !settings.imports.plex_matching {
            return Classification::default();
        }
        let expected = scope(&settings);
        let (index, stale) = {
            let current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
            (
                current
                    .index
                    .as_ref()
                    .filter(|i| i.saved.scope == expected)
                    .cloned(),
                current.restored || current.error.is_some(),
            )
        };
        let Some(index) = index else {
            return Classification {
                warning: Some("Sync Plex classification to match this file".into()),
                ..Classification::default()
            };
        };
        let stale = stale || now_ms().saturating_sub(index.saved.at) >= DAY;
        let mut exact: HashMap<usize, MatchBy> = HashMap::new();
        let mut dav = vec![item.path.clone(), format!("/{}", item.id.ids_path())];
        if let Some(rest) = item.path.strip_prefix("/content/") {
            dav.push(format!("/completed-symlinks/{rest}"));
        }
        for server in selected(&settings) {
            for mapping in &server.path_mappings {
                let (paths, by): (Vec<&str>, MatchBy) = if mapping.dav_path.is_some() {
                    (
                        dav.iter().map(String::as_str).collect(),
                        MatchBy::MappedPath,
                    )
                } else {
                    (
                        links
                            .iter()
                            .filter(|l| {
                                !l.stale
                                    && settings
                                        .imports
                                        .library_dirs
                                        .iter()
                                        .any(|d| d.path == l.root)
                            })
                            .map(|l| l.path.as_str())
                            .collect(),
                        MatchBy::IndexedPath,
                    )
                };
                let prefix = mapping.dav_path.as_ref().or(mapping.local_path.as_ref());
                let Some(prefix) = prefix else {
                    continue;
                };
                for path in paths {
                    let Some(suffix) = path
                        .strip_prefix(prefix.trim_end_matches('/'))
                        .filter(|rest| rest.starts_with('/'))
                    else {
                        continue;
                    };
                    let plex = format!(
                        "{}{suffix}",
                        mapping.plex_path.trim_end_matches(['/', '\\'])
                    );
                    let expected = if mapping.dav_path.is_some() {
                        MappedPath::Dav(path.into())
                    } else {
                        MappedPath::Local(path.into())
                    };
                    if map_path(&plex, &server.path_mappings) != Some(expected) {
                        continue;
                    }
                    if let Some(indices) =
                        index.by_path.get(&(server.id.clone(), normalized(&plex)))
                    {
                        for &i in indices {
                            exact.entry(i).or_insert(by);
                        }
                    }
                }
            }
        }
        if !exact.is_empty() {
            let mut ordered: Vec<_> = exact.into_iter().collect();
            ordered.sort_by_key(|(i, _)| *i);
            let total_matches = u32::try_from(ordered.len()).unwrap_or(u32::MAX);
            return Classification {
                updated_at: Some(index.saved.at),
                stale,
                total_matches,
                matches: ordered
                    .into_iter().take(MAX_MATCHES)
                    .map(|(i, by)| Matching::new(&index.saved.entries[i], by))
                    .collect(),
                warning: (usize::try_from(total_matches).unwrap_or(usize::MAX)>MAX_MATCHES).then(||"Showing the first 100 Plex matches; select a server to narrow classification".into()),
            };
        }
        let mut candidates = HashSet::new();
        for name in std::iter::once(item.name.as_str())
            .chain(links.iter().map(|l| l.path.as_str()))
            .filter_map(filename)
        {
            if let Some(indices) = index.by_name.get(&name) {
                candidates.extend(indices.iter().copied());
            }
        }
        let mut candidates: Vec<_> = candidates.into_iter().collect();
        candidates.sort_unstable();
        if let Some(&first) = candidates.first()
            && candidates
                .iter()
                .any(|&i| !same(&index.saved.entries[first], &index.saved.entries[i]))
        {
            return Classification {
                updated_at: Some(index.saved.at),
                stale,
                warning: Some(
                    "Plex filename matches are ambiguous; configure exact path mappings".into(),
                ),
                matches: Vec::new(),
                total_matches: 0,
            };
        }
        let total_matches = u32::try_from(candidates.len()).unwrap_or(u32::MAX);
        Classification {
            updated_at: Some(index.saved.at),
            stale,
            total_matches,
            warning: (candidates.len() > MAX_MATCHES).then(|| {
                "Showing the first 100 Plex matches; select a server to narrow classification"
                    .into()
            }),
            matches: candidates
                .into_iter()
                .take(MAX_MATCHES)
                .map(|i| Matching::new(&index.saved.entries[i], MatchBy::Filename))
                .collect(),
        }
    }
}
fn same(a: &Entry, b: &Entry) -> bool {
    a.kind == b.kind
        && (a.kind != "episode"
            || if a.server == b.server {
                a.show == b.show
            } else {
                a.show_title.as_ref().is_some_and(|title| !title.is_empty())
                    && b.show_title.as_ref().is_some_and(|title| !title.is_empty())
            })
        && a.title.eq_ignore_ascii_case(&b.title)
        && a.show_title.as_deref().map(str::to_lowercase)
            == b.show_title.as_deref().map(str::to_lowercase)
        && a.season == b.season
        && a.episode == b.episode
        && a.year == b.year
}
#[derive(Serialize, ToSchema)]
#[schema(as=PlexClassificationStatus)]
pub struct Status {
    pub enabled: bool,
    pub servers: u32,
    pub stale: bool,
    pub refreshing: bool,
    pub updated_at: Option<i64>,
    pub entries: u32,
    pub error: Option<String>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PlexMatchBy)]
pub enum MatchBy {
    MappedPath,
    IndexedPath,
    Filename,
}
#[derive(Debug, Serialize, ToSchema)]
#[schema(as=PlexClassificationMatch)]
pub struct Matching {
    pub server: String,
    pub library: String,
    pub key: String,
    pub kind: String,
    pub title: String,
    pub show_title: Option<String>,
    pub season: Option<u32>,
    pub episode: Option<u32>,
    pub year: Option<u32>,
    pub matched_by: MatchBy,
}
impl Matching {
    fn new(e: &Entry, matched_by: MatchBy) -> Self {
        Self {
            server: e.server.clone(),
            library: e.library.clone(),
            key: e.key.clone(),
            kind: e.kind.clone(),
            title: e.title.clone(),
            show_title: e.show_title.clone(),
            season: e.season,
            episode: e.episode,
            year: e.year,
            matched_by,
        }
    }
}
#[derive(Default, Debug, Serialize, ToSchema)]
#[schema(as=PlexClassification)]
pub struct Classification {
    pub updated_at: Option<i64>,
    pub stale: bool,
    pub warning: Option<String>,
    pub matches: Vec<Matching>,
    pub total_matches: u32,
}
#[utoipa::path(get,operation_id="library_plex_status",path="/api/v1/library/plex",responses((status=200,body=Status)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Json<Status> {
    Json(admin.library_metadata.status().await)
}
#[utoipa::path(post,operation_id="library_plex_sync",path="/api/v1/library/plex",responses((status=200,body=Status)))]
pub async fn sync(State(admin): State<Arc<Admin>>) -> Result<Json<Status>, Fail> {
    admin.library_metadata.tick(true).await.map_err(|_| {
        Fail::Unavailable(
            "Plex classification could not be saved; previous snapshot retained".into(),
        )
    })?;
    Ok(Json(admin.library_metadata.status().await))
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
mod tests {
    use super::*;
    use axum::{
        Router,
        body::Body,
        http::{HeaderMap, Response, StatusCode, Uri},
    };
    use infinidysk_core::{ItemId, ItemKind};
    use infinidysk_plex::{PathMapping, Token};
    use std::{
        path::PathBuf,
        sync::atomic::{AtomicBool, AtomicUsize, Ordering},
    };
    use tokio::sync::Notify;
    #[derive(Default)]
    struct Remote {
        mode: AtomicUsize,
        calls: AtomicUsize,
        entered: Notify,
        release: Notify,
    }
    async fn handle(
        State(remote): State<Arc<Remote>>,
        uri: Uri,
        headers: HeaderMap,
    ) -> Response<Body> {
        remote.calls.fetch_add(1, Ordering::SeqCst);
        assert!(
            !uri.query()
                .unwrap_or_default()
                .to_lowercase()
                .contains("token")
        );
        assert_eq!(headers.get("X-Plex-Token").unwrap(), "fixture-secret");
        let mode = remote.mode.load(Ordering::SeqCst);
        if mode == 1 && uri.path().starts_with("/library/") {
            return Response::builder()
                .status(StatusCode::SERVICE_UNAVAILABLE)
                .body(Body::empty())
                .unwrap();
        }
        let body = match uri.path() {
            "/identity" => {
                "<MediaContainer machineIdentifier=\"machine\" version=\"1\"/>".to_owned()
            }
            "/library/sections" => {
                if mode == 5 {
                    "<MediaContainer size=\"0\" totalSize=\"0\" offset=\"0\"/>".into()
                } else {
                    "<MediaContainer size=\"1\" totalSize=\"1\" offset=\"0\"><Directory key=\"1\" title=\"Fixture library\" type=\"movie\"/></MediaContainer>".into()
                }
            }
            "/library/sections/1/all" => {
                if mode == 4 {
                    remote.entered.notify_one();
                    remote.release.notified().await;
                }
                if matches!(mode, 2 | 6 | 7) {
                    format!(
                        "<MediaContainer totalSize=\"2\" offset=\"0\">{}{}</MediaContainer>",
                        video("one", "Fixture A", &["/plex/A/video.mkv"]),
                        if mode == 2 {
                            video("two", "Fixture B", &["/plex/B/video.mkv"])
                        } else if mode == 7 {
                            video("two", "Fixture A", &["/plex/B/video.mkv"])
                                .replace("year=\"2020\"", "year=\"2021\"")
                        } else {
                            video("two", "Fixture A", &["/plex/B/video.mkv"])
                        }
                    )
                } else if mode == 3 {
                    format!(
                        "<MediaContainer totalSize=\"2\" offset=\"0\">{}</MediaContainer>",
                        video("one", "Partial replacement", &["/plex/one.mkv"])
                    )
                } else {
                    format!(
                        "<MediaContainer totalSize=\"1\" offset=\"0\">{}</MediaContainer>",
                        video(
                            "one",
                            "Fixture A",
                            &["/plex/one.mkv", "/plex/alternate.mkv"]
                        )
                    )
                }
            }
            other => panic!("unexpected fixture path {other}"),
        };
        Response::builder()
            .header("content-type", "application/xml")
            .body(Body::from(body))
            .unwrap()
    }
    fn video(key: &str, title: &str, files: &[&str]) -> String {
        let parts = files.iter().fold(String::new(), |mut out, path| {
            write!(out, "<Media><Part file=\"{path}\"/></Media>").unwrap();
            out
        });
        format!(
            "<Video ratingKey=\"{key}\" type=\"movie\" title=\"{title}\" year=\"2020\" viewCount=\"2\">{parts}</Video>"
        )
    }
    struct Fixture {
        service: Arc<Metadata>,
        remote: Arc<Remote>,
        config: Arc<RwLock<ConfigStore>>,
        db: Db,
        task: tokio::task::JoinHandle<()>,
        path: PathBuf,
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            self.task.abort();
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }
    impl Fixture {
        async fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "infinidysk-classification-{}",
                uuid::Uuid::new_v4()
            ));
            std::fs::create_dir_all(&path).unwrap();
            let db = Db::connect(&format!("sqlite:{}", path.join("db.sqlite").display()))
                .await
                .unwrap();
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            let remote = Arc::new(Remote::default());
            let router = Router::new()
                .fallback(handle)
                .with_state(Arc::clone(&remote));
            let task = tokio::spawn(async move {
                axum::serve(listener, router).await.unwrap();
            });
            let mut store = ConfigStore::load(db.clone(), []).await.unwrap();
            let mut s = store.settings().clone();
            s.imports.plex_matching = true;
            s.plex.servers.push(Server {
                id: "machine".into(),
                name: "Fixture".into(),
                url: format!("http://127.0.0.1:{port}"),
                token: Token("fixture-secret".into()),
                enabled: true,
                account_id: None,
                path_mappings: vec![PathMapping {
                    plex_path: "/plex".into(),
                    dav_path: Some("/content".into()),
                    local_path: None,
                }],
            });
            store.save(s).await.unwrap();
            let config = Arc::new(RwLock::new(store));
            let service = Metadata::load(
                db.clone(),
                Arc::clone(&config),
                Client::loopback(uuid::Uuid::new_v4(), port).unwrap(),
            )
            .await
            .unwrap();
            Self {
                service,
                remote,
                config,
                db,
                task,
                path,
            }
        }
        async fn item(&self, name: &str) -> Item {
            let root = self.db.item(ItemId::CONTENT).await.unwrap().unwrap();
            Item::child(&root, name, ItemKind::NzbFile).unwrap()
        }
    }
    #[tokio::test]
    async fn all_versions_match_exact_paths_and_snapshot_excludes_credentials_and_owner_watched_state()
     {
        let f = Fixture::new().await;
        f.service.tick(true).await.unwrap();
        for name in ["one.mkv", "alternate.mkv"] {
            let matched = f.service.match_file(&f.item(name).await, &[]).await;
            assert_eq!(matched.matches.len(), 1);
            assert_eq!(matched.matches[0].matched_by, MatchBy::MappedPath);
            assert_eq!(matched.matches[0].title, "Fixture A");
            assert!(!matched.stale);
        }
        let raw = f.db.plex_classification().await.unwrap().unwrap();
        assert!(!raw.contains("fixture-secret"));
        assert!(!raw.contains("view_count"));
        assert!(!raw.contains("viewCount"));
        let saved: Saved = serde_json::from_str(&raw).unwrap();
        assert_eq!(saved.entries[0].files.len(), 2);
        assert_eq!(saved.entries[0].year, Some(2020));
        assert!(
            !f.db
                .config()
                .await
                .unwrap()
                .iter()
                .any(|(k, _)| k.starts_with("fork_library_metadata")),
            "large snapshot is separate from settings loading"
        );
    }
    #[tokio::test]
    async fn incomplete_pages_and_unavailable_or_inaccessible_libraries_retain_complete_snapshot() {
        let f = Fixture::new().await;
        f.service.tick(true).await.unwrap();
        let before = f.db.plex_classification().await.unwrap();
        for mode in [1, 3, 5] {
            f.remote.mode.store(mode, Ordering::SeqCst);
            f.service.tick(true).await.unwrap();
            assert_eq!(f.db.plex_classification().await.unwrap(), before);
            let matched = f.service.match_file(&f.item("one.mkv").await, &[]).await;
            assert_eq!(matched.matches[0].title, "Fixture A");
            assert!(matched.stale);
            assert!(f.service.status().await.error.is_some());
        }
    }
    #[tokio::test]
    async fn ambiguous_filenames_do_not_override_exact_indexed_library_paths() {
        let f = Fixture::new().await;
        f.remote.mode.store(2, Ordering::SeqCst);
        let mut settings = f.config.read().await.settings().clone();
        settings
            .imports
            .library_dirs
            .push(crate::config::LibraryDir {
                path: "/local".into(),
                arr_path: String::new(),
            });
        settings.plex.servers[0].path_mappings = vec![PathMapping {
            plex_path: "/plex".into(),
            dav_path: None,
            local_path: Some("/local".into()),
        }];
        f.config.write().await.save(settings).await.unwrap();
        f.service.tick(true).await.unwrap();
        let item = f.item("video.mkv").await;
        let ambiguous = f.service.match_file(&item, &[]).await;
        assert_eq!(ambiguous.matches.len(), 0);
        assert!(ambiguous.warning.is_some());
        let link = super::super::catalogue::FileView {
            root: "/local".into(),
            path: "/local/A/video.mkv".into(),
            relative_path: "A/video.mkv".into(),
            group: "A".into(),
            item: item.id.0,
            link_kind: "symlink".into(),
            stale: false,
        };
        let precise = f
            .service
            .match_file(&item, std::slice::from_ref(&link))
            .await;
        assert_eq!(precise.matches.len(), 1);
        assert_eq!(precise.matches[0].title, "Fixture A");
        assert_eq!(precise.matches[0].matched_by, MatchBy::IndexedPath);
        let stale = super::super::catalogue::FileView {
            stale: true,
            ..link
        };
        assert_eq!(f.service.match_file(&item, &[stale]).await.matches.len(), 0);
    }
    #[tokio::test]
    async fn filename_fallback_requires_all_metadata_candidates_to_agree() {
        let f = Fixture::new().await;
        let item = f.item("video.mkv").await;
        f.remote.mode.store(6, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        let agreed = f.service.match_file(&item, &[]).await;
        assert_eq!(agreed.matches.len(), 2);
        assert!(
            agreed
                .matches
                .iter()
                .all(|m| m.matched_by == MatchBy::Filename)
        );
        f.remote.mode.store(7, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        let ambiguous = f.service.match_file(&item, &[]).await;
        assert_eq!(ambiguous.matches.len(), 0);
        assert!(ambiguous.warning.is_some());
    }
    #[tokio::test]
    async fn restored_results_are_stale_and_configuration_changes_block_in_flight_publication() {
        let f = Fixture::new().await;
        f.service.tick(true).await.unwrap();
        let restored = Metadata::load(
            f.db.clone(),
            Arc::clone(&f.config),
            f.service.client.clone(),
        )
        .await
        .unwrap();
        assert!(restored.status().await.stale);
        assert_eq!(
            restored
                .match_file(&f.item("one.mkv").await, &[])
                .await
                .matches
                .len(),
            1
        );
        let before = f.db.plex_classification().await.unwrap();
        f.remote.mode.store(4, Ordering::SeqCst);
        let running = tokio::spawn({
            let service = Arc::clone(&f.service);
            async move { service.tick(true).await }
        });
        tokio::time::timeout(Duration::from_secs(5), f.remote.entered.notified())
            .await
            .unwrap();
        let mut settings = f.config.read().await.settings().clone();
        settings.imports.plex_matching = false;
        f.config.write().await.save(settings).await.unwrap();
        f.remote.release.notify_one();
        assert!(running.await.unwrap().is_err());
        assert_eq!(f.db.plex_classification().await.unwrap(), before);
        assert_eq!(
            f.service
                .match_file(&f.item("one.mkv").await, &[])
                .await
                .matches
                .len(),
            0
        );
    }
    #[tokio::test]
    async fn disabled_matching_and_explicit_empty_server_selection_make_no_remote_requests() {
        let f = Fixture::new().await;
        for disabled in [true, false] {
            let mut settings = f.config.read().await.settings().clone();
            settings.imports.plex_matching = !disabled;
            settings.imports.plex_server_ids = Some(Vec::new());
            f.config.write().await.save(settings).await.unwrap();
            f.service.tick(true).await.unwrap();
            assert_eq!(f.remote.calls.load(Ordering::SeqCst), 0);
            assert_eq!(f.service.status().await.servers, 0);
        }
    }
    #[tokio::test]
    async fn match_responses_are_bounded_and_keep_a_stable_order() {
        let f = Fixture::new().await;
        let entries = (0..105)
            .map(|n| Entry {
                server: "machine".into(),
                library: "1".into(),
                key: format!("fixture-{n}"),
                kind: "movie".into(),
                title: "Fixture A".into(),
                show: None,
                show_title: None,
                season: None,
                episode: None,
                year: Some(2020),
                files: vec!["/plex/one.mkv".into()],
            })
            .collect();
        let expected = scope(f.config.read().await.settings());
        f.service.publish(entries, &expected, true).await.unwrap();
        let item = f.item("one.mkv").await;
        let first = f.service.match_file(&item, &[]).await;
        assert_eq!(first.total_matches, 105);
        assert_eq!(first.matches.len(), 100);
        assert!(first.warning.is_some());
        let second = f.service.match_file(&item, &[]).await;
        assert_eq!(
            first.matches.iter().map(|m| &m.key).collect::<Vec<_>>(),
            second.matches.iter().map(|m| &m.key).collect::<Vec<_>>()
        );
    }
    #[tokio::test]
    async fn background_sync_waits_for_foreground_and_discards_a_pass_overtaken_by_playback() {
        let f = Fixture::new().await;
        let active = Arc::new(AtomicBool::new(true));
        f.service.set_busy({
            let active = Arc::clone(&active);
            Arc::new(move || active.load(Ordering::SeqCst))
        });
        f.service.tick(false).await.unwrap();
        assert_eq!(f.remote.calls.load(Ordering::SeqCst), 0);
        let expected = scope(f.config.read().await.settings());
        assert!(
            f.service
                .publish(Vec::new(), &expected, false)
                .await
                .is_err()
        );
        assert!(f.db.plex_classification().await.unwrap().is_none());
        active.store(false, Ordering::SeqCst);
        f.remote.mode.store(4, Ordering::SeqCst);
        let running = tokio::spawn({
            let service = Arc::clone(&f.service);
            async move { service.tick(false).await }
        });
        f.remote.entered.notified().await;
        active.store(true, Ordering::SeqCst);
        f.remote.release.notify_one();
        running.await.unwrap().unwrap();
        assert_eq!(f.service.status().await.updated_at, None);
        f.remote.mode.store(0, Ordering::SeqCst);
        f.service.tick(true).await.unwrap();
        assert!(
            f.service.status().await.updated_at.is_some(),
            "explicit administrative sync may run while playback is active"
        );
    }
    #[tokio::test]
    async fn an_abandoned_blocking_build_keeps_its_memory_budget_until_actual_completion() {
        let f = Fixture::new().await;
        f.service.tick(true).await.unwrap();
        let before = f.db.plex_classification().await.unwrap();
        let permit = Arc::clone(&f.service.index_slots)
            .acquire_owned()
            .await
            .unwrap();
        let started = Arc::new(Notify::new());
        let worker_started = Arc::clone(&started);
        let (release, wait) = std::sync::mpsc::channel();
        let job = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            worker_started.notify_one();
            let _ = wait.recv();
        });
        started.notified().await;
        job.abort();
        let expected = scope(f.config.read().await.settings());
        assert!(
            f.service
                .publish(Vec::new(), &expected, true)
                .await
                .is_err()
        );
        assert_eq!(f.db.plex_classification().await.unwrap(), before);
        release.send(()).unwrap();
        job.await.unwrap();
        f.service
            .publish(Vec::new(), &expected, true)
            .await
            .unwrap();
    }
}
