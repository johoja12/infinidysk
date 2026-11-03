//! Read-only pointer discovery. Snapshot publication is atomic and configuration-bound;
//! an incomplete NAS walk cannot erase a previously complete index.
use super::{Known, Library, classify};
use crate::{
    admin::{Admin, Fail},
    config::Settings,
};
use axum::{
    Json,
    extract::{Query, State},
};
use futures_util::{StreamExt as _, TryStreamExt as _};
use infinidysk_core::media;
use infinidysk_db::{catalogue::Link, now_ms};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fmt::Write as _;
use std::{
    fs, io,
    os::unix::fs::MetadataExt as _,
    path::{Path, PathBuf},
    sync::Arc,
    time::Duration,
};
use tokio_util::sync::CancellationToken;
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

const INTERVAL: i64 = 15 * 60 * 1000;
// ponytail: per-root complete snapshots are capped at 50,000 links/150,000 entries;
// larger roots need a durable paged walk and staged generation publication.
const MAX_WALK: usize = 150_000;
const MAX_LINKS: usize = 50_000;
const MAX_PATH_BYTES: usize = 16 * 1024 * 1024;

fn digest(data: &[u8]) -> String {
    Sha256::digest(data)
        .iter()
        .fold(String::with_capacity(64), |mut s, b| {
            write!(s, "{b:02x}").expect("formatting into String cannot fail");
            s
        })
}
fn scope(root: &str, settings: &Settings, known: &Known) -> Result<String, serde_json::Error> {
    serde_json::to_vec(&(
        root,
        &settings.imports,
        &known.mount_dirs,
        &known.keys,
        known.legacy,
        &known.skip,
    ))
    .map(|b| digest(&b))
}

fn walk(root: &Path, known: &Known, cancel: &CancellationToken) -> io::Result<Vec<super::Found>> {
    let identity = fs::symlink_metadata(root)?;
    let physical_root = fs::canonicalize(root)?;
    if known
        .skip
        .iter()
        .any(|skip| physical_root.starts_with(skip) || root.starts_with(skip))
    {
        return Err(io::Error::other("library root is inside protected storage"));
    }
    if !identity.is_dir() {
        return Err(io::Error::other("library root is not a directory"));
    }
    let mut pending = vec![(root.to_owned(), 0usize)];
    let mut found = Vec::new();
    let mut visited = 0;
    let mut path_bytes = 0;
    while let Some((dir, depth)) = pending.pop() {
        if depth > 64 {
            return Err(io::Error::other(
                "library directory depth exceeds scan bound",
            ));
        }
        for entry in fs::read_dir(dir)? {
            if cancel.is_cancelled() {
                return Err(io::Error::other("library scan cancelled"));
            }
            visited += 1;
            if visited > MAX_WALK {
                return Err(io::Error::other("library scan exceeds entry bound"));
            }
            let path = entry?.path();
            path_bytes += path.as_os_str().len();
            if path_bytes > MAX_PATH_BYTES {
                return Err(io::Error::other("library scan exceeds path memory bound"));
            }
            let meta = fs::symlink_metadata(&path)?;
            if meta.is_dir() {
                let physical = fs::canonicalize(&path)?;
                if !known
                    .skip
                    .iter()
                    .any(|skip| path.starts_with(skip) || physical.starts_with(skip))
                {
                    pending.push((path, depth + 1));
                }
            } else if let Some(link) = classify(&path, &meta, known) {
                path_bytes = path_bytes.saturating_add(link.pointer.text().len());
                if found.len() >= MAX_LINKS || path_bytes > MAX_PATH_BYTES {
                    return Err(io::Error::other("library snapshot exceeds memory bound"));
                }
                found.push(link);
            }
        }
    }
    let current = fs::symlink_metadata(root)?;
    if current.dev() != identity.dev() || current.ino() != identity.ino() {
        return Err(io::Error::other("library root identity changed"));
    }
    found.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(found)
}

fn protected(settings: &Settings, mounts: &[String]) -> Vec<PathBuf> {
    let mut paths: Vec<_> = mounts
        .iter()
        .chain(std::iter::once(&settings.imports.strm_dir))
        .chain(std::iter::once(&settings.native_cache.metadata_path))
        .chain(settings.native_cache.folders.iter().map(|f| &f.path))
        .filter(|s| !s.is_empty())
        .map(PathBuf::from)
        .collect();
    paths.sort();
    paths.dedup();
    paths
}
impl Library {
    /// Read-side catalogue requests do not canonicalize or inspect NAS paths.
    async fn catalogue_known(&self, settings: &Settings) -> Known {
        let previous: Vec<super::PreviousKey> =
            self.stored(super::KEYS_KEY).await.unwrap_or_default();
        let mut mounts = super::mount_dirs(&self.parts.db, settings).await;
        mounts.push(self.parts.mount_dir.trim_end_matches('/').to_owned());
        mounts.retain(|p| !p.is_empty());
        mounts.sort();
        mounts.dedup();
        Known {
            skip: protected(settings, &mounts),
            mount_dirs: mounts,
            keys: std::iter::once(settings.imports.strm_key.0.clone())
                .chain(previous.into_iter().map(|k| k.key))
                .filter(|k| !k.is_empty())
                .collect(),
            legacy: self.legacy_accepted(),
        }
    }
    pub(crate) async fn run_catalogue(self: Arc<Self>) {
        loop {
            self.catalogue_tick().await;
            tokio::select! {()=self.catalogue_wake.notified()=>{},()=tokio::time::sleep(Duration::from_secs(60))=>{}}
        }
    }
    pub(super) async fn catalogue_tick(&self) {
        if (self.parts.busy)() {
            return;
        }
        let Ok(_scan) = self.catalogue_scan.try_lock() else {
            return;
        };
        let settings = self.settings().await;
        let known = self.catalogue_known(&settings).await;
        let Ok(roots) = self.parts.db.catalogue_roots().await else {
            return;
        };
        let now = now_ms();
        let next = settings
            .imports
            .library_dirs
            .iter()
            .filter_map(|dir| {
                let expected = scope(&dir.path, &settings, &known).ok()?;
                let saved = roots.iter().find(|r| r.root == dir.path);
                let due = saved.is_none_or(|r| {
                    (r.scope != expected
                        || r.scanned_at.is_none_or(|t| {
                            now.saturating_sub(t) >= INTERVAL
                                || r.requested_generation > r.completed_generation
                        }))
                        && (r.error.is_none()
                            || now.saturating_sub(r.attempted_at) >= 300_000
                            || r.requested_generation > r.attempted_generation)
                });
                due.then(|| {
                    (
                        dir.path.clone(),
                        expected,
                        saved.map_or(0, |r| r.attempted_at),
                        saved.map_or(0, |r| r.requested_generation),
                    )
                })
            })
            .min_by_key(|(_, _, attempted, _)| *attempted);
        let Some((root, expected, _, generation)) = next else {
            return;
        };
        let cancel = CancellationToken::new();
        let _cancel_on_drop = cancel.clone().drop_guard();
        let result = tokio::time::timeout(
            Duration::from_secs(25),
            self.catalogue_scan_root(&root, &expected, known, cancel.clone(), now, generation),
        )
        .await;
        cancel.cancel();
        if !matches!(result, Ok(Ok(()))) {
            let _ = self
                .parts
                .db
                .catalogue_failed(
                    &root,
                    &expected,
                    "Library scan incomplete; last complete index retained",
                    generation,
                )
                .await;
        }
    }
    async fn catalogue_scan_root(
        &self,
        root: &str,
        expected: &str,
        known: Known,
        cancel: CancellationToken,
        at: i64,
        generation: i64,
    ) -> anyhow::Result<()> {
        let permit = Arc::clone(&self.pointer_reads).try_acquire_owned()?;
        let root_path = PathBuf::from(root);
        let worker_root = root_path.clone();
        let worker = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            walk(&worker_root, &known, &cancel)
        });
        let found = worker.await??;
        let links: Vec<Link> = futures_util::stream::iter(found.into_iter().map(|f| {
            let root_path = &root_path;
            async move {
                let name = f
                    .path
                    .to_str()
                    .ok_or_else(|| anyhow::anyhow!("non-UTF8 library pointer"))?
                    .to_owned();
                let relative = f
                    .path
                    .strip_prefix(root_path)?
                    .to_str()
                    .ok_or_else(|| anyhow::anyhow!("non-UTF8 relative path"))?
                    .to_owned();
                let group = relative.split('/').next().unwrap_or_default().to_owned();
                let item = self.parts.db.item(f.item).await?;
                let video = item.as_ref().is_some_and(|i| media::is_video(&i.name))
                    || media::is_video(&name);
                Ok::<_, anyhow::Error>(Link {
                    root: root.to_owned(),
                    path: name,
                    relative_path: relative,
                    group_name: group,
                    item_id: f.item.0,
                    pointer_hash: digest(f.pointer.text().as_bytes()),
                    link_kind: if f.pointer.is_link() { 1 } else { 2 },
                    video: i16::from(video),
                })
            }
        }))
        .buffered(16)
        .try_collect()
        .await?;
        let _writing = self.parts.db.write_permit().await;
        let config = self.parts.config.read().await;
        let current = config.settings();
        let current_known = self.catalogue_known(current).await;
        if scope(root, current, &current_known)? != expected || (self.parts.busy)() {
            anyhow::bail!("library configuration or foreground activity changed");
        }
        self.parts
            .db
            .catalogue_replace(root, expected, &links, at, generation)
            .await?;
        drop(config);
        Ok(())
    }
}

#[derive(Serialize, ToSchema)]
#[schema(as=LibraryCatalogueRoot)]
pub struct RootView {
    pub root: String,
    pub scanned_at: Option<i64>,
    pub requested_at: i64,
    pub requested_generation: i64,
    pub completed_generation: i64,
    pub stale: bool,
    pub error: Option<String>,
}
pub(crate) async fn roots(admin: &Admin) -> Result<Vec<RootView>, Fail> {
    let settings = admin.library.settings().await;
    let known = admin.library.catalogue_known(&settings).await;
    let stored = admin.vfs.db().catalogue_roots().await?;
    Ok(settings
        .imports
        .library_dirs
        .iter()
        .map(|d| {
            let saved = stored.iter().find(|r| r.root == d.path);
            let expected = scope(&d.path, &settings, &known).ok();
            RootView {
                root: d.path.clone(),
                scanned_at: saved.and_then(|r| r.scanned_at),
                requested_at: saved.map_or(0, |r| r.requested_at),
                requested_generation: saved.map_or(0, |r| r.requested_generation),
                completed_generation: saved.map_or(0, |r| r.completed_generation),
                stale: saved.is_none_or(|r| {
                    r.scanned_at.is_none()
                        || r.requested_generation > r.completed_generation
                        || r.error.is_some()
                        || Some(&r.scope) != expected.as_ref()
                        || now_ms().saturating_sub(r.scanned_at.unwrap_or(0)) > INTERVAL
                }),
                error: saved.and_then(|r| r.error.clone()),
            }
        })
        .collect())
}
#[utoipa::path(get,operation_id="library_catalogue_roots",path="/api/v1/library/catalogue",responses((status=200,body=Vec<RootView>)))]
pub async fn status(State(admin): State<Arc<Admin>>) -> Result<Json<Vec<RootView>>, Fail> {
    Ok(Json(roots(&admin).await?))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=LibraryCatalogueRefresh)]
pub struct Refresh {
    pub root: Option<String>,
}
#[utoipa::path(post,operation_id="library_catalogue_refresh",path="/api/v1/library/catalogue",request_body=Refresh,responses((status=200)))]
pub async fn refresh(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<Refresh>,
) -> Result<(), Fail> {
    let settings = admin.library.settings().await;
    let known = admin.library.catalogue_known(&settings).await;
    if request.root.as_ref().is_some_and(|root| {
        !settings
            .imports
            .library_dirs
            .iter()
            .any(|d| d.path == *root)
    }) {
        return Err(Fail::Bad("Choose a configured library root".into()));
    }
    for dir in settings
        .imports
        .library_dirs
        .iter()
        .filter(|d| request.root.as_ref().is_none_or(|r| r == &d.path))
    {
        let expected = scope(&dir.path, &settings, &known).map_err(|_| Fail::Internal)?;
        admin
            .vfs
            .db()
            .catalogue_request(&dir.path, &expected, now_ms())
            .await?;
    }
    admin.library.catalogue_wake.notify_one();
    Ok(())
}
#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in=Query)]
pub struct Browse {
    pub root: String,
    pub group: Option<String>,
    pub after: Option<String>,
    pub limit: Option<u32>,
}
#[derive(Serialize, ToSchema)]
#[schema(as=LibraryCatalogueGroup)]
pub struct GroupView {
    pub root: String,
    pub group: String,
    pub files: u64,
    pub bytes: u64,
    pub unavailable: u64,
    pub stale: bool,
}
#[utoipa::path(get,operation_id="library_catalogue_groups",path="/api/v1/library/catalogue/groups",params(Browse),responses((status=200,body=Vec<GroupView>)))]
pub async fn groups(
    State(admin): State<Arc<Admin>>,
    Query(q): Query<Browse>,
) -> Result<Json<Vec<GroupView>>, Fail> {
    let root = roots(&admin)
        .await?
        .into_iter()
        .find(|r| r.root == q.root)
        .ok_or(Fail::NotFound)?;
    Ok(Json(
        admin
            .vfs
            .db()
            .catalogue_groups(
                &q.root,
                q.after.as_deref().unwrap_or(""),
                q.limit.unwrap_or(100),
            )
            .await?
            .into_iter()
            .map(|g| GroupView {
                root: g.root,
                group: g.group_name,
                files: g.files.cast_unsigned(),
                bytes: g.bytes.cast_unsigned(),
                unavailable: g.unavailable.cast_unsigned(),
                stale: root.stale,
            })
            .collect(),
    ))
}
#[derive(Serialize, ToSchema)]
#[schema(as=LibraryCatalogueFile)]
pub struct FileView {
    pub root: String,
    pub path: String,
    pub relative_path: String,
    pub group: String,
    pub item: Uuid,
    pub link_kind: String,
    pub stale: bool,
}
impl FileView {
    pub(crate) fn from_link(l: Link, stale: bool) -> Self {
        Self {
            root: l.root,
            path: l.path,
            relative_path: l.relative_path,
            group: l.group_name,
            item: l.item_id,
            link_kind: if l.link_kind == 1 { "symlink" } else { "strm" }.into(),
            stale,
        }
    }
}
#[utoipa::path(get,operation_id="library_catalogue_files",path="/api/v1/library/catalogue/files",params(Browse),responses((status=200,body=Vec<FileView>)))]
pub async fn files(
    State(admin): State<Arc<Admin>>,
    Query(q): Query<Browse>,
) -> Result<Json<Vec<FileView>>, Fail> {
    let root = roots(&admin)
        .await?
        .into_iter()
        .find(|r| r.root == q.root)
        .ok_or(Fail::NotFound)?;
    Ok(Json(
        admin
            .vfs
            .db()
            .catalogue_links(
                Some(&q.root),
                q.group.as_deref(),
                None,
                true,
                q.after.as_deref().unwrap_or(""),
                q.limit.unwrap_or(100),
            )
            .await?
            .into_iter()
            .map(|l| FileView::from_link(l, root.stale))
            .collect(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_core::ItemId;
    #[test]
    fn bounded_walk_recognizes_exact_pointers_and_does_not_follow_directory_links() {
        let dir = std::env::temp_dir().join(format!("infinidysk-catalogue-walk-{}", ItemId::new()));
        fs::create_dir_all(dir.join("Collection")).unwrap();
        fs::create_dir_all(dir.join("skip")).unwrap();
        let id = ItemId::new();
        let known = Known {
            mount_dirs: vec!["/known-mount".into()],
            keys: vec!["key".into()],
            legacy: false,
            skip: vec![dir.join("skip")],
        };
        std::os::unix::fs::symlink(
            format!("/known-mount/{}", id.ids_path()),
            dir.join("Collection/file.mkv"),
        )
        .unwrap();
        std::os::unix::fs::symlink(
            format!("/unknown/{}", id.ids_path()),
            dir.join("Collection/unknown.mkv"),
        )
        .unwrap();
        std::os::unix::fs::symlink(
            format!("/known-mount/{}", id.ids_path()),
            dir.join("skip/ignored.mkv"),
        )
        .unwrap();
        std::os::unix::fs::symlink(&dir, dir.join("loop")).unwrap();
        assert!(walk(&dir.join("skip"), &known, &CancellationToken::new()).is_err());
        let result = walk(&dir, &known, &CancellationToken::new()).unwrap();
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].item, id);
        let cancelled = CancellationToken::new();
        cancelled.cancel();
        assert!(walk(&dir, &known, &cancelled).is_err());
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn changed_mapping_or_signing_authority_invalidates_catalogue_scope() {
        let settings = Settings::default();
        let mut known = Known {
            mount_dirs: vec!["/mount".into()],
            keys: vec!["key".into()],
            legacy: false,
            skip: vec![],
        };
        let original = scope("/library", &settings, &known).unwrap();
        known.keys.push("other".into());
        assert_ne!(scope("/library", &settings, &known).unwrap(), original);
        assert_ne!(scope("/other-root", &settings, &known).unwrap(), original);
    }
}

#[cfg(test)]
mod storage_paths {
    use super::*;
    #[test]
    fn cache_metadata_payloads_and_mounts_are_excluded_without_filesystem_lookups() {
        let mut s = Settings::default();
        s.native_cache.metadata_path = "/not-mounted/metadata".into();
        s.native_cache
            .folders
            .push(infinidysk_native_cache::FolderSettings {
                path: "/not-mounted/payload".into(),
                ..Default::default()
            });
        let paths = protected(&s, &["/not-mounted/mount".into()]);
        for p in [
            "/not-mounted/metadata",
            "/not-mounted/payload",
            "/not-mounted/mount",
        ] {
            assert!(paths.contains(&PathBuf::from(p)));
        }
    }
}
