//! Read-only, complete browser snapshots. Filtering never drops matches beyond page one.
use super::{LibraryFile, resolve_source};
use crate::{
    admin::{Admin, Fail},
    config::Settings,
};
use axum::{
    Json,
    extract::{Path, Query, State},
};
use infinidysk_core::ItemId;
use infinidysk_db::{Item, now_ms};
use infinidysk_plex::{MappedPath, Media, Server, map_path};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    sync::{Arc, Mutex, PoisonError},
};
use tokio::sync::Semaphore;
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

const MAX_ROWS: usize = 150_000;
const MAX_BYTES: usize = 128 * 1024 * 1024;
const TTL: i64 = 300_000;

#[derive(Clone, Serialize, ToSchema)]
#[schema(as=PlexBrowseFile)]
pub struct File {
    pub key: String,
    pub artwork_key: Option<String>,
    pub media: Media,
    pub library: String,
    pub item: Option<Uuid>,
    pub size: Option<u64>,
    pub mapping: String,
    pub quality: String,
    pub group: String,
    pub group_title: String,
    pub matched: bool,
    pub watched_known: bool,
    pub cached_bytes: Option<u64>,
    #[serde(skip)]
    pointer: Option<String>,
}
#[derive(Clone, Serialize, ToSchema)]
#[schema(as=PlexBrowseGroup)]
pub struct Group {
    pub key: String,
    pub title: String,
    pub artwork_key: Option<String>,
    pub kind: String,
    pub year: Option<u32>,
    pub files: usize,
    pub seasons: usize,
    pub last_viewed_at: Option<i64>,
    pub bytes: u64,
    pub cached_bytes: Option<u64>,
    pub attention: usize,
    pub qualities: Vec<String>,
}
#[derive(Clone, Default, Serialize, ToSchema)]
#[schema(as=PlexBrowseTotals)]
pub struct Totals {
    pub files: usize,
    pub internal: usize,
    pub attention: usize,
    pub unmatched: usize,
    pub external: usize,
}
#[derive(Default, Serialize, ToSchema)]
#[schema(as=PlexBrowsePage)]
pub struct Page {
    pub snapshot: Option<String>,
    pub updated_at: Option<i64>,
    pub refreshing: bool,
    pub warning: Option<String>,
    pub coverage_checked: usize,
    pub coverage_total: usize,
    pub coverage_ready: bool,
    pub coverage_at: Option<i64>,
    pub totals: Totals,
    pub matched_files: usize,
    pub total_groups: usize,
    pub files: Vec<File>,
    pub groups: Vec<Group>,
    pub next: Option<u32>,
}
#[derive(Default, Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct Browse {
    pub library: Option<String>,
    pub view: Option<String>,
    pub category: Option<String>,
    pub search: Option<String>,
    pub mapping: Option<String>,
    pub matched: Option<String>,
    pub quality: Option<String>,
    pub cache: Option<String>,
    pub watched: Option<String>,
    pub season: Option<u32>,
    pub group: Option<String>,
    pub sort: Option<String>,
    pub offset: Option<u32>,
    pub limit: Option<u32>,
    pub refresh: Option<bool>,
}
#[derive(Default)]
struct Coverage {
    values: HashMap<Uuid, Option<u64>>,
    checked: usize,
    total: usize,
    ready: bool,
    at: Option<i64>,
}
struct Snapshot {
    scope: String,
    rows: Vec<File>,
    totals: Totals,
    at: i64,
    configuration: String,
    coverage: Mutex<Coverage>,
}
#[derive(Default)]
struct Current {
    snapshots: Vec<Arc<Snapshot>>,
    pending: HashSet<String>,
    errors: HashMap<String, String>,
    retry_at: HashMap<String, i64>,
}
pub struct Cache {
    current: Mutex<Current>,
    metadata: Semaphore,
    coverage: Semaphore,
    artwork: Semaphore,
    pages: Arc<Semaphore>,
}
impl Default for Cache {
    fn default() -> Self {
        Self {
            current: Mutex::default(),
            metadata: Semaphore::new(1),
            coverage: Semaphore::new(1),
            artwork: Semaphore::new(4),
            pages: Arc::new(Semaphore::new(2)),
        }
    }
}
fn digest(bytes: impl AsRef<[u8]>) -> String {
    use std::fmt::Write as _;
    Sha256::digest(bytes.as_ref())
        .iter()
        .fold(String::with_capacity(64), |mut out, byte| {
            let _ = write!(out, "{byte:02x}");
            out
        })
}
fn configuration(settings: &Settings, server: &str, library: &str) -> String {
    digest(
        serde_json::to_vec(&(
            server,
            library,
            &settings.plex,
            &settings.imports,
            &settings.mount.dir,
            &settings.providers,
        ))
        .expect("library configuration contains only serializable values"),
    )
}
impl Current {
    fn publish(&mut self, snapshot: Arc<Snapshot>) {
        // One complete catalogue per configuration. Replaced coverage work must
        // release its slot instead of delaying verification of the new catalogue.
        self.snapshots
            .retain(|s| s.configuration != snapshot.configuration);
        self.snapshots.insert(0, snapshot);
        self.snapshots.truncate(2);
    }
    fn snapshot(&self, key: &str, configuration: &str) -> Option<Arc<Snapshot>> {
        self.snapshots
            .iter()
            .find(|s| s.scope == key && s.configuration == configuration)
            .or_else(|| {
                self.snapshots
                    .iter()
                    .find(|s| s.configuration == configuration)
            })
            .cloned()
    }
}
async fn scope(
    admin: &Admin,
    settings: &Settings,
    server: &str,
    library: &str,
) -> Result<String, Fail> {
    let roots = admin.vfs.db().catalogue_roots().await?;
    let generations: Vec<_> = roots
        .iter()
        .map(|r| (&r.root, &r.scope, r.completed_generation))
        .collect();
    let bytes = serde_json::to_vec(&(
        server,
        library,
        &settings.plex,
        &settings.imports,
        &settings.mount.dir,
        &settings.providers,
        generations,
        admin.vfs.db().blob_epoch(),
    ))
    .map_err(|_| Fail::Internal)?;
    Ok(digest(bytes))
}
impl Cache {
    fn request(
        self: &Arc<Self>,
        admin: Arc<Admin>,
        settings: Settings,
        server: Server,
        library: String,
        key: &str,
        refresh: bool,
    ) -> (Option<Arc<Snapshot>>, bool, Option<String>) {
        let mut current = self.current.lock().unwrap_or_else(PoisonError::into_inner);
        // Catalogue generations and health reports may change while the complete
        // replacement is built. Retain the last catalogue only under identical
        // server, credentials, mappings and library settings. Actions still revalidate.
        let configuration = configuration(&settings, &server.id, &library);
        let snapshot = current.snapshot(key, &configuration);
        // Do not discard an unfinished verification sweep at every metadata TTL.
        // Explicit refresh and changed source scope still request a replacement.
        let expired = snapshot.as_ref().is_none_or(|s| {
            s.scope != key
                || now_ms() - s.at > TTL
                    && s.coverage
                        .lock()
                        .unwrap_or_else(PoisonError::into_inner)
                        .ready
        });
        if (refresh || expired && current.retry_at.get(key).is_none_or(|at| now_ms() >= *at))
            && !current.pending.contains(key)
            && current.pending.len() < 2
        {
            current.pending.insert(key.to_owned());
            let cache = Arc::clone(self);
            let expected = key.to_owned();
            tokio::spawn(async move {
                let work = async {
                    let permit = cache.metadata.acquire().await.map_err(|_| Fail::Internal)?;
                    let built = build(&admin, &settings, &server, &library, &expected).await?;
                    let latest = admin.config.read().await.settings().clone();
                    if scope(&admin, &latest, &server.id, &library).await? != expected {
                        return Err(Fail::Bad("Library settings changed; refresh again".into()));
                    }
                    let snapshot = Arc::new(built);
                    {
                        let mut current =
                            cache.current.lock().unwrap_or_else(PoisonError::into_inner);
                        current.publish(Arc::clone(&snapshot));
                    }
                    drop(permit);
                    let cache = Arc::clone(&cache);
                    let admin = Arc::clone(&admin);
                    // Coverage is optional enrichment. It must not keep metadata
                    // refreshing or prevent another complete catalogue from publishing.
                    tokio::spawn(async move {
                        let work = async {
                            let Ok(_permit) = cache.coverage.acquire().await else {
                                return;
                            };
                            fill_coverage(&admin, &snapshot).await;
                        };
                        tokio::select! { ()=admin.cancel.cancelled()=>{}, ()=work=>{} }
                    });
                    Ok::<_, Fail>(())
                };
                let result = tokio::select! {()=admin.cancel.cancelled()=>Err(Fail::Unavailable("Library refresh stopped".into())), result=work=>result};
                let mut current = cache.current.lock().unwrap_or_else(PoisonError::into_inner);
                current.pending.remove(&expected);
                if let Err(error) = result {
                    let reason = match &error {
                        Fail::Bad(message)
                        | Fail::Conflict(message)
                        | Fail::Unavailable(message)
                        | Fail::Failed(message) => message.as_str(),
                        Fail::NotFound => "Plex server or library is unavailable",
                        Fail::Setting(_, _) => "Library settings are invalid",
                        Fail::Internal => "Library database or internal operation failed",
                    };
                    tracing::warn!(reason, "Plex library refresh deferred");
                    if current.errors.len() >= 16 {
                        current.errors.clear();
                        current.retry_at.clear();
                    }
                    current.retry_at.insert(expected.clone(), now_ms() + 30_000);
                    current.errors.insert(
                        expected,
                        format!(
                            "Library refresh did not complete: {reason}. Previous results retained."
                        ),
                    );
                } else {
                    current.errors.remove(&expected);
                    current.retry_at.remove(&expected);
                }
            });
        }
        (
            snapshot,
            current.pending.contains(key),
            current.errors.get(key).cloned(),
        )
    }
}
fn quality(media: &Media) -> String {
    let resolution = media
        .resolution
        .as_deref()
        .unwrap_or("")
        .to_ascii_lowercase();
    let explicit = match resolution.as_str() {
        "4k" | "2160" | "2160p" => Some("4k"),
        "1080" | "1080p" => Some("1080p"),
        "720" | "720p" => Some("720p"),
        "sd" | "480" | "576" | "360" => Some("sd"),
        _ => None,
    };
    let tokens: Vec<_> = media
        .file
        .as_deref()
        .unwrap_or("")
        .split(|c: char| !c.is_ascii_alphanumeric())
        .map(str::to_ascii_lowercase)
        .collect();
    explicit
        .unwrap_or_else(|| {
            if tokens
                .iter()
                .any(|s| matches!(s.as_str(), "2160p" | "4k" | "uhd"))
            {
                "4k"
            } else if tokens.iter().any(|s| s == "1080p") {
                "1080p"
            } else if tokens.iter().any(|s| s == "720p") {
                "720p"
            } else if tokens
                .iter()
                .any(|s| matches!(s.as_str(), "sd" | "480p" | "576p" | "360p"))
            {
                "sd"
            } else {
                "unknown"
            }
        })
        .into()
}

fn make_file(
    media: Media,
    library: String,
    item: Option<&Item>,
    mapping: &str,
    pointer: Option<String>,
    matched: bool,
) -> File {
    let group = if media.kind == "episode" {
        format!(
            "show/{}",
            media.show.as_deref().unwrap_or(&media.rating_key)
        )
    } else {
        format!("movie/{}", media.rating_key)
    };
    let group_title = media
        .show_title
        .clone()
        .unwrap_or_else(|| media.title.clone());
    File {
        key: digest(format!(
            "{}\0{}\0{}",
            library,
            media.rating_key,
            media.file.as_deref().unwrap_or("")
        )),
        artwork_key: if !media.artwork {
            None
        } else if media.kind == "episode" {
            media.show.clone()
        } else {
            (!media.rating_key.starts_with("native:")).then(|| media.rating_key.clone())
        },
        quality: quality(&media),
        media,
        library,
        item: item.map(|i| i.id.0),
        size: item.and_then(|i| i.size),
        mapping: mapping.into(),
        group,
        group_title,
        matched,
        watched_known: pointer.is_none(),
        cached_bytes: None,
        pointer,
    }
}
async fn build(
    admin: &Admin,
    settings: &Settings,
    server: &Server,
    library: &str,
    key: &str,
) -> Result<Snapshot, Fail> {
    admin
        .plex
        .client
        .test_server(server)
        .await
        .map_err(super::fail)?;
    let libraries: Vec<_> = admin
        .plex
        .client
        .libraries(server)
        .await
        .map_err(super::fail)?
        .into_iter()
        .filter(|l| {
            matches!(l.kind.as_str(), "movie" | "show") && (library == "all" || library == l.id)
        })
        .collect();
    if libraries.is_empty() {
        return Err(Fail::NotFound);
    }
    let configured: HashSet<_> = settings
        .imports
        .catalogue_dirs()
        .map(|d| d.path.as_str())
        .collect();
    let mut pointers = HashMap::new();
    let mut links = Vec::new();
    let mut after = String::new();
    loop {
        let page = admin
            .vfs
            .db()
            .catalogue_links(None, None, None, true, &after, 1000)
            .await?;
        if page.is_empty() {
            break;
        }
        for l in &page {
            if configured.contains(l.root.as_str()) {
                pointers.insert(l.path.clone(), l.item_id);
                links.push(l.clone());
            }
        }
        if links.len() > MAX_ROWS {
            return Err(Fail::Unavailable(
                "Library index exceeds browser capacity".into(),
            ));
        }
        after = page.last().ok_or(Fail::Internal)?.path.clone();
        if page.len() < 1000 {
            break;
        }
        tokio::task::yield_now().await;
    }
    let ids: Vec<_> = pointers
        .values()
        .copied()
        .collect::<HashSet<_>>()
        .into_iter()
        .collect();
    let mut items = HashMap::new();
    for batch in ids.chunks(1000) {
        for i in admin.vfs.db().items_by_ids(batch).await? {
            items.insert(i.id.0, i);
        }
    }
    let mut rows = Vec::new();
    let mut represented = HashSet::new();
    let mut bytes = 0;
    let mut probes = 0;
    for lib in libraries {
        let all = admin
            .plex
            .client
            .catalogue(server, &lib)
            .await
            .map_err(super::fail)?;
        for entry in all {
            let paths = if entry.files.is_empty() {
                vec![None]
            } else {
                entry.files.into_iter().map(Some).collect()
            };
            for path in paths {
                let mut media = entry.media.clone();
                media.file.clone_from(&path);
                media.year = entry.year;
                let mapped = path
                    .as_deref()
                    .and_then(|p| map_path(p, &server.path_mappings));
                let mut item = None;
                let mut mapping = "external";
                match &mapped {
                    Some(MappedPath::Local(local)) => {
                        if let Some(id) = pointers.get(local) {
                            item = items
                                .get(id)
                                .filter(|i| i.health_state != infinidysk_db::HealthState::Hidden)
                                .cloned();
                            mapping = if item.is_some() { "internal" } else { "broken" };
                        // ponytail: probe at most 32 unindexed paths per snapshot; a full
                        // native pointer-index refresh resolves the remaining unknown rows.
                        } else if probes < 32 {
                            probes += 1;
                            match resolve_source(admin, server, &media).await {
                                Ok(found) => {
                                    item = found;
                                    mapping = if item.is_some() {
                                        "internal"
                                    } else {
                                        "external"
                                    };
                                }
                                Err(_) => mapping = "unknown",
                            }
                        } else {
                            mapping = "unknown";
                        }
                    }
                    Some(MappedPath::Dav(_)) => {
                        item = resolve_source(admin, server, &media).await?;
                        mapping = if item.is_some() { "internal" } else { "broken" };
                    }
                    None => {}
                }
                if item.is_some()
                    && let Some(MappedPath::Local(path)) = &mapped
                {
                    represented.insert(path.clone());
                }
                let mut row = make_file(media, lib.id.clone(), item.as_ref(), mapping, None, true);
                if row.size.is_none() {
                    row.size = path.as_ref().and_then(|p| entry.file_sizes.get(p)).copied();
                }
                bytes += serde_json::to_vec(&row).map_err(|_| Fail::Internal)?.len();
                rows.push(row);
                if rows.len() > MAX_ROWS || bytes > MAX_BYTES {
                    return Err(Fail::Unavailable(
                        "Plex library exceeds browser capacity".into(),
                    ));
                }
            }
            tokio::task::yield_now().await;
        }
    }
    // Indexed sources without a Plex path match stay visible, including unmatched files.
    // Filename classification is descriptive only; actions revalidate the pointer.
    if library == "all" {
        for link in links {
            if represented.contains(&link.path) {
                continue;
            }
            let item = items
                .get(&link.item_id)
                .filter(|i| i.health_state != infinidysk_db::HealthState::Hidden);
            let class = if let Some(i) = item {
                admin
                    .library_metadata
                    .match_file(
                        i,
                        &[crate::library::catalogue::FileView::from_link(
                            link.clone(),
                            false,
                        )],
                    )
                    .await
            } else {
                crate::library::metadata::Classification::default()
            };
            let matched = class.matches.iter().find(|m| m.server == server.id);
            let media = Media {
                artwork: false,
                year: matched.and_then(|m| m.year),
                resolution: None,
                rating_key: format!("native:{}", link.item_id),
                kind: matched.map_or("unknown", |m| m.kind.as_str()).into(),
                title: matched.map_or_else(
                    || item.map_or_else(|| link.relative_path.clone(), |i| i.name.clone()),
                    |m| m.title.clone(),
                ),
                show: matched.and_then(|m| m.show.clone()),
                show_title: matched.and_then(|m| m.show_title.clone()),
                season: matched.and_then(|m| m.season),
                episode: matched.and_then(|m| m.episode),
                file: Some(link.path.clone()),
                view_count: 0,
                view_offset_ms: 0,
                duration_ms: 0,
                viewed_at: None,
                user: None,
            };
            let mut row = make_file(
                media,
                "indexed".into(),
                item,
                if item.is_some() { "internal" } else { "broken" },
                Some(link.path),
                matched.is_some(),
            );
            if let Some(m) = matched {
                row.artwork_key = if m.kind == "episode" {
                    m.show.clone()
                } else {
                    Some(m.key.clone())
                };
            }
            bytes += serde_json::to_vec(&row).map_err(|_| Fail::Internal)?.len();
            rows.push(row);
            if rows.len() > MAX_ROWS || bytes > MAX_BYTES {
                return Err(Fail::Unavailable(
                    "Combined library exceeds browser capacity".into(),
                ));
            }
            tokio::task::yield_now().await;
        }
    }
    let totals = Totals {
        files: rows.len(),
        internal: rows.iter().filter(|r| r.mapping == "internal").count(),
        attention: rows
            .iter()
            .filter(|r| matches!(r.mapping.as_str(), "broken" | "unknown"))
            .count(),
        unmatched: rows.iter().filter(|r| !r.matched).count(),
        external: rows.iter().filter(|r| r.mapping == "external").count(),
    };
    let total = rows
        .iter()
        .filter_map(|r| r.item)
        .collect::<HashSet<_>>()
        .len();
    Ok(Snapshot {
        scope: key.into(),
        rows,
        totals,
        at: now_ms(),
        configuration: configuration(settings, &server.id, library),
        coverage: Mutex::new(Coverage {
            total,
            ..Coverage::default()
        }),
    })
}
async fn fill_coverage(admin: &Admin, snapshot: &Snapshot) {
    let ids: HashSet<_> = snapshot.rows.iter().filter_map(|r| r.item).collect();
    let cache = admin.vfs.native_cache();
    let legacy_expected = admin
        .config
        .read()
        .await
        .settings()
        .native_cache
        .legacy
        .is_some();
    let candidates = if let Some(cache) = cache.filter(|cache| {
        !legacy_expected
            || cache
                .legacy_reader()
                .and_then(|reader| reader.catalogue())
                .is_some()
    }) {
        cache.cached_items().await.ok()
    } else {
        // A configured original store which has not opened is unknown, never empty.
        None
    };
    for id in ids {
        if !admin
            .plex
            .browser
            .current
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .snapshots
            .iter()
            .any(|s| std::ptr::eq(s.as_ref(), snapshot))
        {
            return;
        }
        let value = if let (Some(cache), Some(candidates)) = (cache, candidates.as_ref()) {
            if candidates.contains(&id) {
                let work = async {
                    let item = admin.vfs.db().item(ItemId(id)).await.ok()??;
                    let key = admin.vfs.cache_key(&item).await.ok()?;
                    let ranges = cache.coverage(&key).await.ok()?;
                    Some(ranges.iter().map(|r| r.end - r.start).sum())
                };
                tokio::time::timeout(std::time::Duration::from_secs(5), work)
                    .await
                    .ok()
                    .flatten()
            } else {
                Some(0)
            }
        } else {
            None
        };
        {
            let mut coverage = snapshot
                .coverage
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            coverage.values.insert(id, value);
            coverage.checked += 1;
        }
        tokio::task::yield_now().await;
    }
    let mut coverage = snapshot
        .coverage
        .lock()
        .unwrap_or_else(PoisonError::into_inner);
    coverage.ready = true;
    coverage.at = Some(now_ms());
}
fn matches(row: &File, q: &Browse, cached_bytes: Option<u64>) -> bool {
    let search = q.search.as_deref().unwrap_or("").to_lowercase();
    (search.is_empty()
        || format!(
            "{} {} {}",
            row.media.title,
            row.group_title,
            row.media.file.as_deref().unwrap_or("")
        )
        .to_lowercase()
        .contains(&search))
        && q.mapping
            .as_deref()
            .is_none_or(|v| v == "all" || v == row.mapping)
        && q.matched
            .as_deref()
            .is_none_or(|v| v == "all" || (v == "matched") == row.matched)
        && q.quality
            .as_deref()
            .is_none_or(|v| v == "all" || v == row.quality)
        && q.category.as_deref().is_none_or(|v| {
            v == "all"
                || (v == "movies" && row.media.kind == "movie")
                || (v == "shows" && row.media.kind == "episode")
                || (v == "unmatched" && !row.matched)
        })
        && q.group.as_deref().is_none_or(|v| v == row.group)
        && q.season.is_none_or(|v| row.media.season == Some(v))
        && q.watched.as_deref().is_none_or(|v| {
            v == "all"
                || row.watched_known
                    && ((v == "watched" && row.media.view_count > 0)
                        || (v == "unwatched" && row.media.view_count == 0)
                        || (v == "in-progress"
                            && row.media.view_offset_ms > 0
                            && row.media.view_offset_ms < row.media.duration_ms))
        })
        && q.cache.as_deref().is_none_or(|v| {
            v == "all"
                || match (cached_bytes, row.size) {
                    (Some(n), Some(size)) => match v {
                        "any" => n > 0,
                        "complete" => size > 0 && n >= size,
                        "empty" => n == 0,
                        _ => false,
                    },
                    _ => v == "unavailable",
                }
        })
}
#[derive(Clone, Copy)]
struct Row<'a> {
    file: &'a File,
    cached_bytes: Option<u64>,
}
impl std::ops::Deref for Row<'_> {
    type Target = File;
    fn deref(&self) -> &File {
        self.file
    }
}
struct GroupBuilder<'a> {
    group: Group,
    first: &'a File,
    seasons: HashSet<u32>,
    qualities: HashSet<&'a str>,
}
fn group(rows: &[Row<'_>]) -> Vec<Group> {
    let mut groups = HashMap::<&str, GroupBuilder<'_>>::new();
    for row in rows {
        let builder = groups
            .entry(row.group.as_str())
            .or_insert_with(|| GroupBuilder {
                first: row.file,
                group: Group {
                    key: row.group.clone(),
                    title: row.group_title.clone(),
                    artwork_key: None,
                    kind: row.media.kind.clone(),
                    year: row.media.year,
                    files: 0,
                    seasons: 0,
                    last_viewed_at: None,
                    bytes: 0,
                    cached_bytes: Some(0),
                    attention: 0,
                    qualities: Vec::new(),
                },
                seasons: HashSet::new(),
                qualities: HashSet::new(),
            });
        let g = &mut builder.group;
        // Match the first episode in title order without sorting every file.
        let order = |file: &File| (file.media.season, file.media.episode);
        if order(row.file) < order(builder.first)
            || order(row.file) == order(builder.first) && row.key < builder.first.key
        {
            builder.first = row.file;
            g.year = row.media.year;
        }
        g.files += 1;
        g.bytes = g.bytes.saturating_add(row.size.unwrap_or(0));
        g.cached_bytes = g
            .cached_bytes
            .zip(row.cached_bytes)
            .map(|(a, b)| a.saturating_add(b));
        g.last_viewed_at = g.last_viewed_at.max(row.media.viewed_at);
        g.attention += usize::from(matches!(row.mapping.as_str(), "broken" | "unknown"));
        if g.artwork_key.is_none() {
            g.artwork_key.clone_from(&row.artwork_key);
        }
        if let Some(season) = row.media.season {
            builder.seasons.insert(season);
        }
        builder.qualities.insert(row.quality.as_str());
    }
    groups
        .into_values()
        .map(|mut builder| {
            builder.group.seasons = builder.seasons.len();
            let mut qualities: Vec<_> = builder.qualities.into_iter().map(str::to_owned).collect();
            qualities.sort();
            builder.group.qualities = qualities;
            builder.group
        })
        .collect()
}
#[utoipa::path(get,operation_id="plex_library_browser",path="/api/v1/plex/servers/{id}/browse",params(("id"=String,Path),Browse),responses((status=200,body=Page)))]
pub async fn browse(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
    Query(q): Query<Browse>,
) -> Result<Json<Page>, Fail> {
    if q.library.as_ref().is_some_and(|v| v.len() > 256)
        || q.search.as_ref().is_some_and(|s| s.len() > 256)
        || q.group.as_ref().is_some_and(|s| s.len() > 256)
        || q.offset.unwrap_or(0) as usize > MAX_ROWS
        || q.season.is_some_and(|s| s > 1000)
    {
        return Err(Fail::Bad("Invalid library filter".into()));
    }
    for (value, allowed) in [
        (q.view.as_deref(), &["files", "groups"][..]),
        (
            q.category.as_deref(),
            &["all", "movies", "shows", "unmatched"],
        ),
        (
            q.mapping.as_deref(),
            &["all", "internal", "external", "broken", "unknown"],
        ),
        (q.matched.as_deref(), &["all", "matched", "unmatched"]),
        (
            q.quality.as_deref(),
            &["all", "4k", "1080p", "720p", "sd", "unknown"],
        ),
        (
            q.cache.as_deref(),
            &["all", "any", "complete", "empty", "unavailable"],
        ),
        (
            q.watched.as_deref(),
            &["all", "watched", "unwatched", "in-progress"],
        ),
        (q.sort.as_deref(), &["title", "recent", "size", "coverage"]),
    ] {
        if value.is_some_and(|v| !allowed.contains(&v)) {
            return Err(Fail::Bad("Unknown library filter".into()));
        }
    }
    let settings = admin.config.read().await.settings().clone();
    let server = settings
        .plex
        .servers
        .iter()
        .find(|s| s.id == id && s.enabled)
        .cloned()
        .ok_or(Fail::NotFound)?;
    let library = q.library.as_deref().unwrap_or("all");
    let key = scope(&admin, &settings, &id, library).await?;
    let (snapshot, refreshing, warning) = admin.plex.browser.request(
        Arc::clone(&admin),
        settings,
        server,
        library.into(),
        &key,
        q.refresh.unwrap_or(false),
    );
    let result = Page {
        snapshot: None,
        updated_at: None,
        refreshing,
        warning,
        coverage_checked: 0,
        coverage_total: 0,
        coverage_ready: false,
        coverage_at: None,
        totals: Totals::default(),
        matched_files: 0,
        total_groups: 0,
        files: vec![],
        groups: vec![],
        next: None,
    };
    let Some(snapshot) = snapshot else {
        return Ok(Json(result));
    };
    let permit = admin
        .plex
        .browser
        .pages
        .clone()
        .acquire_owned()
        .await
        .map_err(|_| Fail::Internal)?;
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        Json(render(result, &snapshot, &q))
    })
    .await
    .map_err(|_| Fail::Internal)
}
fn render(mut result: Page, snapshot: &Snapshot, q: &Browse) -> Page {
    result.snapshot = Some(snapshot.scope.clone());
    result.updated_at = Some(snapshot.at);
    result.totals = snapshot.totals.clone();
    let coverage = snapshot
        .coverage
        .lock()
        .unwrap_or_else(PoisonError::into_inner);
    result.coverage_checked = coverage.checked;
    result.coverage_total = coverage.total;
    result.coverage_ready = coverage.ready;
    result.coverage_at = coverage.at;
    if !coverage.ready && q.cache.as_deref().is_some_and(|v| v != "all") {
        return result;
    }
    // Keep the catalogue borrowed. Only the requested file page is cloned.
    let mut rows: Vec<_> = snapshot
        .rows
        .iter()
        .map(|file| Row {
            file,
            cached_bytes: file
                .item
                .and_then(|id| coverage.values.get(&id).copied().flatten()),
        })
        .filter(|r| matches(r.file, q, r.cached_bytes))
        .collect();
    drop(coverage);
    result.matched_files = rows.len();
    let mut groups = group(&rows);
    match q.sort.as_deref().unwrap_or("title") {
        "size" => groups.sort_by(|a, b| b.bytes.cmp(&a.bytes).then_with(|| a.key.cmp(&b.key))),
        "coverage" => groups.sort_by(|a, b| {
            b.cached_bytes
                .cmp(&a.cached_bytes)
                .then_with(|| a.key.cmp(&b.key))
        }),
        "recent" => groups.sort_by(|a, b| {
            b.last_viewed_at
                .cmp(&a.last_viewed_at)
                .then_with(|| a.key.cmp(&b.key))
        }),
        _ => groups.sort_by_cached_key(|g| (g.title.to_lowercase(), g.key.clone())),
    }
    result.total_groups = groups.len();
    let offset = q.offset.unwrap_or(0) as usize;
    let limit = q.limit.unwrap_or(50).clamp(1, 100) as usize;
    if q.view.as_deref() == Some("groups") && q.group.is_none() {
        if groups.len() > offset + limit {
            result.next = u32::try_from(offset + limit).ok();
        }
        result.groups = groups.into_iter().skip(offset).take(limit).collect();
    } else {
        if rows.len() > offset + limit {
            result.next = u32::try_from(offset + limit).ok();
        }
        match q.sort.as_deref().unwrap_or("title") {
            "recent" => rows.sort_by(|a, b| {
                b.media
                    .viewed_at
                    .cmp(&a.media.viewed_at)
                    .then_with(|| a.key.cmp(&b.key))
            }),
            "size" => rows.sort_by(|a, b| b.size.cmp(&a.size).then_with(|| a.key.cmp(&b.key))),
            "coverage" => rows.sort_by(|a, b| {
                b.cached_bytes
                    .cmp(&a.cached_bytes)
                    .then_with(|| a.key.cmp(&b.key))
            }),
            _ => rows.sort_by_cached_key(|r| {
                (
                    r.group_title.to_lowercase(),
                    r.media.season,
                    r.media.episode,
                    r.key.clone(),
                )
            }),
        }
        result.files = rows
            .into_iter()
            .skip(offset)
            .take(limit)
            .map(|r| {
                let mut file = r.file.clone();
                file.cached_bytes = r.cached_bytes;
                file
            })
            .collect();
    }
    result
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PlexBrowseResolve)]
pub struct Resolve {
    pub snapshot: String,
    pub key: String,
}
#[utoipa::path(post,operation_id="plex_library_resolve",path="/api/v1/plex/servers/{id}/browse/resolve",params(("id"=String,Path)),request_body=Resolve,responses((status=200,body=LibraryFile)))]
pub async fn resolve(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
    Json(q): Json<Resolve>,
) -> Result<Json<LibraryFile>, Fail> {
    if q.snapshot.len() != 64 || q.key.len() != 64 {
        return Err(Fail::Bad("Invalid library selection".into()));
    }
    let settings = admin.config.read().await.settings().clone();
    let server = settings
        .plex
        .servers
        .iter()
        .find(|s| s.id == id && s.enabled)
        .ok_or(Fail::NotFound)?;
    let row = {
        let current = admin
            .plex
            .browser
            .current
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        current
            .snapshots
            .iter()
            .find(|s| s.scope == q.snapshot)
            .and_then(|s| s.rows.iter().find(|r| r.key == q.key))
            .cloned()
            .ok_or(Fail::NotFound)?
    };
    let library = if row.library == "indexed" {
        "all"
    } else {
        row.library.as_str()
    };
    // A full-server snapshot can contain per-library rows; accept only one of those two scopes.
    let expected_all = scope(&admin, &settings, &id, "all").await?;
    let expected_library = scope(&admin, &settings, &id, library).await?;
    if q.snapshot != expected_all && q.snapshot != expected_library {
        return Err(Fail::Bad(
            "Library settings changed; refresh before opening this file".into(),
        ));
    }
    let (media, item) = if let Some(pointer) = &row.pointer {
        let resolved = admin
            .library
            .prediction_pointer(pointer.clone())
            .await
            .map_err(|()| Fail::Unavailable("Library pointer is unavailable".into()))?;
        if resolved.map(|i| i.0) != row.item {
            return Err(Fail::Bad(
                "Library pointer changed; refresh before opening".into(),
            ));
        }
        (
            row.media,
            if let Some(item) = resolved {
                admin
                    .vfs
                    .db()
                    .item(item)
                    .await?
                    .filter(|i| i.health_state != infinidysk_db::HealthState::Hidden)
            } else {
                None
            },
        )
    } else {
        let path = row
            .media
            .file
            .as_deref()
            .ok_or_else(|| Fail::Bad("This Plex record has no exact file version".into()))?;
        let media = admin
            .plex
            .client
            .metadata_variant(server, &row.media.rating_key, path)
            .await
            .map_err(super::fail)?;
        let item = resolve_source(&admin, server, &media).await?;
        (media, item)
    };
    Ok(Json(LibraryFile {
        media,
        item: item.as_ref().map(|i| i.id.0),
        size: item.and_then(|i| i.size),
    }))
}

#[utoipa::path(get,operation_id="plex_library_artwork",path="/api/v1/plex/servers/{id}/artwork/{key}",params(("id"=String,Path),("key"=String,Path)),responses((status=200,description="Plex artwork"),(status=204,description="No artwork")))]
pub async fn artwork(
    State(admin): State<Arc<Admin>>,
    Path((id, key)): Path<(String, String)>,
) -> Result<axum::response::Response, Fail> {
    use axum::response::IntoResponse as _;
    if key.is_empty()
        || key.len() > 256
        || !key
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        return Err(Fail::Bad("Invalid Plex artwork identity".into()));
    }
    let server = admin
        .config
        .read()
        .await
        .settings()
        .plex
        .servers
        .iter()
        .find(|s| s.id == id && s.enabled)
        .cloned()
        .ok_or(Fail::NotFound)?;
    let _permit = admin
        .plex
        .browser
        .artwork
        .acquire()
        .await
        .map_err(|_| Fail::Internal)?;
    Ok(match admin.plex.client.artwork(&server, &key).await {
        Ok((bytes, content_type)) => (
            [
                (axum::http::header::CONTENT_TYPE, content_type),
                (axum::http::header::CACHE_CONTROL, "private, max-age=300"),
            ],
            bytes,
        )
            .into_response(),
        Err(_) => axum::http::StatusCode::NO_CONTENT.into_response(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn row(title: &str, path: &str, episode: u32) -> File {
        let media: Media = serde_json::from_value(serde_json::json!({
            "rating_key": episode.to_string(), "kind":"episode", "title": title,
            "show":"show-1", "show_title":"Fixture Show", "season":2,"episode":episode,
            "file":path,"view_count":0,"view_offset_ms":0,"duration_ms":1000,
            "viewed_at":null,"user":null
        }))
        .unwrap();
        let mut row = make_file(media, "fixture".into(), None, "internal", None, true);
        row.size = Some(100);
        row.cached_bytes = Some(50);
        row
    }
    #[test]
    fn catalogue_filters_find_late_rows_and_keep_unknown_distinct() {
        let mut rows: Vec<_> = (0..125)
            .map(|i| row("Fixture", "/media/file.1080p.mkv", i))
            .collect();
        rows[124].media.file = Some("/media/needle.mkv".into());
        let mut q = Browse {
            search: Some("needle".into()),
            season: Some(2),
            cache: Some("any".into()),
            ..Default::default()
        };
        let found: Vec<_> = rows
            .iter()
            .filter(|r| matches(r, &q, r.cached_bytes))
            .collect();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].media.episode, Some(124));
        let mut unknown = rows[124].clone();
        unknown.cached_bytes = None;
        assert!(!matches(&unknown, &q, unknown.cached_bytes));
        q.cache = Some("unavailable".into());
        assert!(matches(&unknown, &q, unknown.cached_bytes));
        unknown.watched_known = false;
        q.watched = Some("unwatched".into());
        assert!(!matches(&unknown, &q, unknown.cached_bytes));
    }
    #[test]
    fn grouping_aggregates_all_files_and_preserves_unknown_coverage() {
        let a = row("Episode one", "/file.1080p.mkv", 1);
        let mut b = row("Episode two", "/file.2160p.mkv", 2);
        b.media.season = Some(3);
        b.cached_bytes = None;
        b.mapping = "broken".into();
        let groups = group(&[
            Row {
                file: &a,
                cached_bytes: a.cached_bytes,
            },
            Row {
                file: &b,
                cached_bytes: b.cached_bytes,
            },
        ]);
        assert_eq!(groups.len(), 1);
        let g = &groups[0];
        assert_eq!((g.files, g.seasons, g.bytes, g.attention), (2, 2, 200, 1));
        assert_eq!(g.cached_bytes, None);
        assert_eq!(g.qualities, ["1080p", "4k"]);
    }
    fn snapshot(rows: Vec<File>) -> Snapshot {
        Snapshot {
            scope: "fixture-content".into(),
            configuration: "fixture-settings".into(),
            totals: Totals {
                files: rows.len(),
                ..Totals::default()
            },
            rows,
            at: now_ms(),
            coverage: Mutex::new(Coverage {
                ready: true,
                ..Coverage::default()
            }),
        }
    }
    #[test]
    fn rendering_filters_the_complete_catalogue_before_paging_and_preserves_unknown_coverage() {
        let mut rows: Vec<_> = (0..125)
            .map(|i| row("Fixture", &format!("/media/{i}.mkv"), i))
            .collect();
        rows[124].media.title = "fixture needle".into();
        let snapshot = snapshot(rows);
        let q = Browse {
            view: Some("files".into()),
            search: Some("needle".into()),
            ..Browse::default()
        };
        let page = render(Page::default(), &snapshot, &q);
        assert_eq!(page.matched_files, 1);
        assert_eq!(page.files[0].media.episode, Some(124));
        assert_eq!(page.files[0].cached_bytes, None);
        let q = Browse {
            view: Some("files".into()),
            offset: Some(100),
            limit: Some(10),
            ..Browse::default()
        };
        let page = render(Page::default(), &snapshot, &q);
        assert_eq!(page.matched_files, 125);
        assert_eq!(page.total_groups, 1);
        assert_eq!(page.files.len(), 10);
        assert_eq!(page.files[0].media.episode, Some(100));
        assert_eq!(page.next, Some(110));
        let q = Browse {
            view: Some("groups".into()),
            ..Browse::default()
        };
        let page = render(Page::default(), &snapshot, &q);
        assert_eq!(page.groups[0].files, 125);
        assert_eq!(page.groups[0].cached_bytes, None);
    }
    #[test]
    fn catalogue_changes_retain_a_complete_snapshot_only_with_identical_configuration() {
        let saved = Arc::new(snapshot(vec![row("Fixture", "/media/fixture.mkv", 1)]));
        let mut current = Current {
            snapshots: vec![Arc::clone(&saved)],
            ..Current::default()
        };
        assert!(Arc::ptr_eq(
            &current.snapshot("new-content", "fixture-settings").unwrap(),
            &saved
        ));
        assert!(
            current
                .snapshot("new-content", "changed-credentials-or-mappings")
                .is_none()
        );
        let mut replacement = snapshot(Vec::new());
        replacement.scope = "new-content".into();
        let replacement = Arc::new(replacement);
        current.publish(Arc::clone(&replacement));
        assert_eq!(current.snapshots.len(), 1);
        assert!(Arc::ptr_eq(&current.snapshots[0], &replacement));
        assert!(!current.snapshots.iter().any(|s| Arc::ptr_eq(s, &saved)));
        let mut settings = Settings::default();
        let before = configuration(&settings, "server", "all");
        settings.mount.dir.push_str("/changed");
        assert_ne!(configuration(&settings, "server", "all"), before);
        assert_ne!(configuration(&settings, "another-server", "all"), before);
        assert_ne!(configuration(&Settings::default(), "server", "1"), before);
    }
    #[test]
    #[ignore = "explicit generated catalogue performance measurement"]
    fn generated_catalogue_render_timing() {
        let rows = (0..54_428)
            .map(|i| {
                let mut file = row("Fixture", &format!("/media/fixture-{i}.mkv"), i);
                file.group = format!("fixture-group-{:05}", i % 8810);
                file.group_title = file.group.clone();
                file
            })
            .collect();
        let snapshot = snapshot(rows);
        let q = Browse {
            view: Some("groups".into()),
            limit: Some(50),
            ..Browse::default()
        };
        let started = std::time::Instant::now();
        for _ in 0..5 {
            let page = render(Page::default(), &snapshot, &q);
            assert_eq!(
                (page.matched_files, page.total_groups, page.groups.len()),
                (54_428, 8810, 50)
            );
        }
        println!(
            "generated catalogue mean render ms: {}",
            started.elapsed().as_millis() / 5
        );
    }
    #[test]
    fn resolution_metadata_wins_and_sd_is_a_token() {
        let mut f = row("Fixture", "/media/SDR.Movie.1080p.mkv", 1);
        assert_eq!(f.quality, "1080p");
        f.media.file = Some("/media/SDR.Movie.mkv".into());
        assert_eq!(quality(&f.media), "unknown");
        f.media.resolution = Some("4k".into());
        assert_eq!(quality(&f.media), "4k");
    }
}
