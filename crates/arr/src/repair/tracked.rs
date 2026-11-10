//! Mutation checkpoints are persisted before dispatch. A lost response is observed,
//! never permission to replay a blocklist, deletion or search blindly.
use super::{Episode, GRABBED, Media, MediaFile, Movie, Owner, Page, replacing};
use crate::{Arr, Error, Session, fetch, send};
use reqwest::Method;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    future::Future,
    time::{Duration, SystemTime, UNIX_EPOCH},
};
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RegrabStage {
    Prepared,
    BlocklistPending,
    Blocklisted,
    DeletePending,
    Deleted,
    SearchPending,
    Submitted,
    Replaced,
}
impl RegrabStage {
    pub const fn label(self) -> &'static str {
        match self {
            Self::Prepared => "prepared",
            Self::BlocklistPending => "blocklist_pending",
            Self::Blocklisted => "blocklisted",
            Self::DeletePending => "delete_pending",
            Self::Deleted => "deleted",
            Self::SearchPending => "search_pending",
            Self::Submitted => "submitted",
            Self::Replaced => "replaced",
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Regrab {
    pub version: u8,
    pub owner: Owner,
    pub stage: RegrabStage,
    pub command: Option<i64>,
    before_command: i64,
    wait_until: i64,
    #[serde(default)]
    search_since: Option<i64>,
}
impl Regrab {
    pub fn new(owner: &Owner) -> Self {
        Self {
            version: 1,
            owner: owner.clone(),
            stage: RegrabStage::Prepared,
            command: None,
            before_command: 0,
            wait_until: 0,
            search_since: None,
        }
    }
    pub fn terminal(&self) -> bool {
        self.stage == RegrabStage::Replaced
    }
    pub fn searches(&self) -> (&'static str, &[i64]) {
        self.owner.searches()
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum Target {
    Same,
    Gone,
    Replaced,
}
fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(i64::MAX)
}
async fn target(s: &Session<'_>, owner: &Owner) -> Result<Target, Error> {
    let old = owner.current.as_ref().map(|f| f.id).or(owner.recorded_file);
    let path = owner
        .current
        .as_ref()
        .map_or(owner.imported_path.as_str(), |f| f.path.as_str());
    let current = match &owner.media {
        Media::Movie(id) => {
            fetch::<Movie>(s.call(Method::GET, &format!("movie/{id}")))
                .await?
                .movie_file
        }
        Media::Episodes { ids, .. } => {
            if ids.is_empty() || ids.len() > 256 {
                return Err(Error::Unexpected(
                    "episode regrab target count is outside its bound",
                ));
            }
            let mut files = std::collections::HashSet::new();
            for id in ids {
                let episode: Episode = fetch(s.call(Method::GET, &format!("episode/{id}"))).await?;
                if episode.file_id != 0 {
                    files.insert(episode.file_id);
                }
            }
            if files.is_empty() {
                None
            } else if files.len() != 1 {
                return Err(Error::Unexpected("episode ownership changed during regrab"));
            } else {
                let file = *files.iter().next().expect("one file was checked");
                Some(fetch::<MediaFile>(s.call(Method::GET, &format!("episodefile/{file}"))).await?)
            }
        }
        Media::Book { id, .. } => {
            let files: Vec<MediaFile> =
                fetch(s.call(Method::GET, "bookfile").query(&[("bookId", *id)])).await?;
            let last_import = owner.imported_at.as_deref().and_then(|d| d.parse().ok());
            super::current_book_file(files, path, last_import, old)
        }
        Media::Album { .. } => {
            // An album may contain multiple tracks; only inspect the exact recorded file.
            let Some(id) = old else {
                return Err(Error::Unexpected("track regrab needs an exact file id"));
            };
            match fetch::<MediaFile>(s.call(Method::GET, &format!("trackfile/{id}"))).await {
                Err(Error::Rejected {
                    status: reqwest::StatusCode::NOT_FOUND,
                    ..
                }) => None,
                result => Some(result?),
            }
        }
    };
    match current {
        None => Ok(Target::Gone),
        Some(file) if file.id <= 0 => Err(Error::Unexpected(
            "Arr returned an invalid replacement file id",
        )),
        Some(file) if Some(file.id) != old => Ok(Target::Replaced),
        Some(file) if file.path != path => Err(Error::Unexpected(
            "file path changed during regrab; review before mutation",
        )),
        Some(_) => Ok(Target::Same),
    }
}
/// Arr owns the filesystem operation. Require its current root-folder authority
/// before dispatch; display/catalogue filename matches never authorize mutation.
async fn library_authority(s: &Session<'_>, owner: &Owner) -> Result<(), Error> {
    fn normalized(path: &str) -> Option<String> {
        if path.len() > 4096 || path.chars().any(char::is_control) {
            return None;
        }
        let path = path.replace('\\', "/");
        let drive =
            path.as_bytes().get(1) == Some(&b':') && path.as_bytes()[0].is_ascii_alphabetic();
        if !(path.starts_with('/') || drive && path.as_bytes().get(2) == Some(&b'/')) {
            return None;
        }
        if path.split('/').any(|p| p == "." || p == "..") {
            return None;
        }
        Some(if drive {
            path.to_ascii_lowercase()
        } else {
            path
        })
    }
    let path = normalized(
        owner
            .current
            .as_ref()
            .map_or(owner.imported_path.as_str(), |f| f.path.as_str()),
    )
    .ok_or(Error::Unexpected(
        "regrab path is not normalized and absolute",
    ))?;
    let roots: Vec<Value> = fetch(s.call(Method::GET, "rootfolder")).await?;
    if roots.len() > 1000 {
        return Err(Error::Unexpected("Arr root-folder list exceeds its bound"));
    }
    let mut allowed = false;
    for root in roots {
        let root = root
            .get("path")
            .and_then(Value::as_str)
            .and_then(normalized)
            .ok_or(Error::Unexpected("Arr returned an invalid library root"))?;
        let root = root.trim_end_matches('/');
        if root.len() > 1
            && path
                .strip_prefix(root)
                .is_some_and(|tail| tail.starts_with('/'))
        {
            allowed = true;
        }
    }
    if !allowed {
        return Err(Error::Unexpected(
            "regrab path is outside the current Arr library roots",
        ));
    }
    Ok(())
}
async fn save<F, Fut>(current: &mut Regrab, next: Regrab, checkpoint: &F) -> Result<(), Error>
where
    F: Fn(Regrab) -> Fut,
    Fut: Future<Output = Result<(), Error>>,
{
    checkpoint(next.clone()).await?;
    *current = next;
    Ok(())
}
fn search(owner: &Owner) -> Value {
    match &owner.media {
        Media::Movie(id) => json!({"name":"MoviesSearch","movieIds":[id]}),
        Media::Episodes { ids, .. } => json!({"name":"EpisodeSearch","episodeIds":ids}),
        Media::Album { id, .. } => json!({"name":"AlbumSearch","albumIds":[id]}),
        Media::Book { id, .. } => json!({"name":"BookSearch","bookIds":[id]}),
    }
}
async fn commands(s: &Session<'_>) -> Result<Vec<Value>, Error> {
    let rows: Vec<Value> = fetch(s.call(Method::GET, "command")).await?;
    let mut ids = std::collections::HashSet::new();
    if rows.iter().any(|row| {
        row.get("id")
            .and_then(Value::as_i64)
            .is_none_or(|id| id <= 0 || !ids.insert(id))
    }) {
        return Err(Error::Unexpected(
            "command observation has invalid or repeated identities",
        ));
    }
    if rows.len() > 1000 {
        return Err(Error::Unexpected("command observation exceeded its bound"));
    }
    Ok(rows)
}
fn matches_command(value: &Value, expected: &Value) -> bool {
    let body = value.get("body").unwrap_or(value);
    let name = body
        .get("name")
        .or_else(|| value.get("name"))
        .and_then(Value::as_str);
    if !name.is_some_and(|n| {
        expected["name"]
            .as_str()
            .is_some_and(|e| e.eq_ignore_ascii_case(n))
    }) {
        return false;
    }
    let key = if expected.get("movieIds").is_some() {
        "movieIds"
    } else if expected.get("episodeIds").is_some() {
        "episodeIds"
    } else if expected.get("bookIds").is_some() {
        "bookIds"
    } else {
        "albumIds"
    };
    let ids = |v: &Value| -> Option<Vec<i64>> {
        let mut ids = v
            .get(key)?
            .as_array()?
            .iter()
            .map(Value::as_i64)
            .collect::<Option<Vec<_>>>()?;
        ids.sort_unstable();
        Some(ids)
    };
    ids(body).is_some_and(|got| Some(got) == ids(expected))
}
/// The journal and callback belong to the shared repair coordinator. No credential is
/// serialized here. The callback must reject stale versions before allowing dispatch.
pub async fn replace_tracked<F, Fut>(
    http: &reqwest::Client,
    arr: &Arr,
    progress: &mut Regrab,
    wait: Duration,
    checkpoint: F,
) -> Result<(), Error>
where
    F: Fn(Regrab) -> Fut,
    Fut: Future<Output = Result<(), Error>>,
{
    if progress.version != 1 {
        return Err(Error::Unexpected("unsupported regrab journal version"));
    }
    if progress.stage == RegrabStage::Replaced {
        return Ok(());
    }
    if progress.owner.replaced() {
        let mut next = progress.clone();
        next.stage = RegrabStage::Replaced;
        save(progress, next, &checkpoint).await?;
        return Ok(());
    }
    let s = Session::open(http, arr).await?;
    let observed = target(&s, &progress.owner).await?;
    if observed == Target::Replaced {
        let mut next = progress.clone();
        next.stage = RegrabStage::Replaced;
        save(progress, next, &checkpoint).await?;
        return Ok(());
    }
    if progress.stage == RegrabStage::Submitted {
        return Ok(());
    }
    if observed == Target::Same
        && matches!(
            progress.stage,
            RegrabStage::Deleted | RegrabStage::SearchPending
        )
    {
        return Err(Error::Unexpected(
            "deleted target reappeared; review before further mutation",
        ));
    }
    if progress.stage == RegrabStage::Prepared {
        library_authority(&s, &progress.owner).await?;
    }
    if replacing(&s, progress.owner.download_id(), &progress.owner).await? {
        let mut next = progress.clone();
        next.stage = RegrabStage::Submitted;
        save(progress, next, &checkpoint).await?;
        return Ok(());
    }
    if progress.stage == RegrabStage::BlocklistPending {
        let Some(job) = progress.owner.download_id() else {
            return Err(Error::Unexpected("blocklist journal lacks provenance"));
        };
        let failed: Page = fetch(s.call(Method::GET, "history").query(&[
            ("downloadId", job),
            ("eventType", "4"),
            ("pageSize", "1"),
        ]))
        .await?;
        if !failed.records.iter().any(|r| {
            r.download_id
                .as_deref()
                .is_some_and(|d| d.eq_ignore_ascii_case(job))
        }) {
            return Err(Error::Uncertain(
                "blocklist outcome is unknown; awaiting observation",
            ));
        }
        let mut next = progress.clone();
        next.stage = RegrabStage::Blocklisted;
        save(progress, next, &checkpoint).await?;
    }
    if progress.stage == RegrabStage::Prepared {
        if let Some(job) = progress.owner.download_id() {
            let grabbed: Page = fetch(s.call(Method::GET, "history").query(&[
                ("downloadId", job),
                ("eventType", GRABBED),
                ("pageSize", "1"),
            ]))
            .await?;
            if let Some(grab) = grabbed.records.first() {
                if grab
                    .download_id
                    .as_deref()
                    .is_some_and(|id| !id.eq_ignore_ascii_case(job))
                {
                    return Err(Error::Unexpected(
                        "grab history returned a different download",
                    ));
                }
                let mut next = progress.clone();
                next.stage = RegrabStage::BlocklistPending;
                next.search_since = Some(
                    super::newest_grabs(&s, &progress.owner)
                        .await?
                        .iter()
                        .map(|r| r.id)
                        .max()
                        .unwrap_or(0),
                );
                next.wait_until =
                    now().saturating_add(wait.as_millis().try_into().unwrap_or(i64::MAX));
                library_authority(&s, &progress.owner).await?;
                save(progress, next, &checkpoint).await?;
                send(
                    s.call(Method::POST, &format!("history/failed/{}", grab.id))
                        .json(&json!({})),
                )
                .await?;
                let mut next = progress.clone();
                next.stage = RegrabStage::Blocklisted;
                save(progress, next, &checkpoint).await?;
            }
        }
        if progress.stage == RegrabStage::Prepared {
            let mut next = progress.clone();
            next.stage = RegrabStage::Blocklisted;
            save(progress, next, &checkpoint).await?;
        }
    }
    if progress.stage == RegrabStage::Blocklisted {
        if let Some(job) = progress.owner.download_id() {
            // Existing journals lack a baseline. Observe conservatively: ongoing own
            // searches still hold deletion, but old history cannot prove a new grab.
            let since = progress.search_since.unwrap_or(i64::MAX);
            if super::own_search(&s, Some(job), &progress.owner, since, wait).await? {
                let mut next = progress.clone();
                next.stage = RegrabStage::Submitted;
                save(progress, next, &checkpoint).await?;
                return Ok(());
            }
        }
        if progress.owner.download_id().is_none()
            && super::own_search(&s, None, &progress.owner, i64::MAX, wait).await?
        {
            let mut next = progress.clone();
            next.stage = RegrabStage::Submitted;
            save(progress, next, &checkpoint).await?;
            return Ok(());
        }
        if progress.search_since.is_none() && now() < progress.wait_until {
            return Err(Error::Uncertain(
                "blocklist accepted; automatic search observation pending",
            ));
        }
        if replacing(&s, progress.owner.download_id(), &progress.owner).await? {
            let mut next = progress.clone();
            next.stage = RegrabStage::Submitted;
            save(progress, next, &checkpoint).await?;
            return Ok(());
        }
        let observed = target(&s, &progress.owner).await?;
        if observed == Target::Replaced {
            let mut next = progress.clone();
            next.stage = RegrabStage::Replaced;
            save(progress, next, &checkpoint).await?;
            return Ok(());
        }
        let mut next = progress.clone();
        next.stage = if observed == Target::Gone {
            RegrabStage::Deleted
        } else {
            RegrabStage::DeletePending
        };
        if next.stage == RegrabStage::DeletePending {
            library_authority(&s, &progress.owner).await?;
        }
        save(progress, next, &checkpoint).await?;
        if progress.stage == RegrabStage::DeletePending {
            let file = progress
                .owner
                .current
                .as_ref()
                .map(|f| f.id)
                .or(progress.owner.recorded_file)
                .ok_or(Error::Unexpected(
                    "deletion needs an exact original file id",
                ))?;
            let kind = match progress.owner.media {
                Media::Movie(_) => "moviefile",
                Media::Episodes { .. } => "episodefile",
                Media::Album { .. } => "trackfile",
                Media::Book { .. } => "bookfile",
            };
            let result = send(s.call(Method::DELETE, &format!("{kind}/{file}"))).await;
            // Even a successful response must be verified before progressing. Any failure
            // is uncertain until the next read-only observation of the same target.
            result?;
        }
    }
    if progress.stage == RegrabStage::DeletePending {
        match target(&s, &progress.owner).await? {
            Target::Same => {
                return Err(Error::Uncertain(
                    "deletion outcome is unknown; original target still visible",
                ));
            }
            Target::Gone => {
                let mut next = progress.clone();
                next.stage = RegrabStage::Deleted;
                save(progress, next, &checkpoint).await?;
            }
            Target::Replaced => {
                let mut next = progress.clone();
                next.stage = RegrabStage::Replaced;
                save(progress, next, &checkpoint).await?;
                return Ok(());
            }
        }
    }
    let expected = search(&progress.owner);
    if progress.stage == RegrabStage::SearchPending {
        if let Some(command) = commands(&s).await?.iter().find(|v| {
            v.get("id")
                .and_then(Value::as_i64)
                .is_some_and(|id| id > progress.before_command)
                && matches_command(v, &expected)
        }) {
            let mut next = progress.clone();
            next.stage = RegrabStage::Submitted;
            next.command = command.get("id").and_then(Value::as_i64);
            save(progress, next, &checkpoint).await?;
            return Ok(());
        }
        return Err(Error::Uncertain(
            "search submission outcome is unknown; awaiting Arr observation",
        ));
    }
    if progress.stage == RegrabStage::Deleted {
        let before = commands(&s)
            .await?
            .iter()
            .filter_map(|v| v.get("id").and_then(Value::as_i64))
            .max()
            .unwrap_or(0);
        let mut next = progress.clone();
        next.stage = RegrabStage::SearchPending;
        next.before_command = before;
        library_authority(&s, &progress.owner).await?;
        save(progress, next, &checkpoint).await?;
        // A 2xx body may be malformed after the command was accepted. Keep SearchPending
        // in that case and reconcile by command id/arguments on the next pass.
        let command: Value = fetch(s.call(Method::POST, "command").json(&expected)).await?;
        let id = command
            .get("id")
            .and_then(Value::as_i64)
            .ok_or(Error::Uncertain(
                "search accepted without a verifiable command id",
            ))?;
        if id <= before {
            return Err(Error::Uncertain("search returned an old command id"));
        }
        let mut next = progress.clone();
        next.stage = RegrabStage::Submitted;
        next.command = Some(id);
        save(progress, next, &checkpoint).await?;
    }
    Ok(())
}

/// Advances only facts established by GETs. It never dispatches a mutation.
pub async fn observe_tracked(
    http: &reqwest::Client,
    arr: &Arr,
    progress: &mut Regrab,
) -> Result<(), Error> {
    if progress.version != 1 {
        return Err(Error::Unexpected("unsupported regrab journal version"));
    }
    let s = Session::open(http, arr).await?;
    let current = target(&s, &progress.owner).await?;
    if current == Target::Replaced {
        progress.stage = RegrabStage::Replaced;
        return Ok(());
    }
    match progress.stage {
        RegrabStage::DeletePending if current == Target::Gone => {
            progress.stage = RegrabStage::Deleted;
        }
        RegrabStage::SearchPending => {
            if let Some(command) = commands(&s).await?.iter().find(|v| {
                v.get("id")
                    .and_then(Value::as_i64)
                    .is_some_and(|id| id > progress.before_command)
                    && matches_command(v, &search(&progress.owner))
            }) {
                progress.stage = RegrabStage::Submitted;
                progress.command = command.get("id").and_then(Value::as_i64);
            }
        }
        RegrabStage::BlocklistPending => {
            if let Some(job) = progress.owner.download_id() {
                let failed: Page = fetch(s.call(Method::GET, "history").query(&[
                    ("downloadId", job),
                    ("eventType", "4"),
                    ("pageSize", "1"),
                ]))
                .await?;
                if failed.records.iter().any(|r| {
                    r.download_id
                        .as_deref()
                        .is_some_and(|id| id.eq_ignore_ascii_case(job))
                }) {
                    progress.stage = RegrabStage::Blocklisted;
                }
            }
        }
        _ => {}
    }
    Ok(())
}

/// Observe an outstanding replacement without dispatching any mutation.
pub async fn regrab_replaced(
    http: &reqwest::Client,
    arr: &Arr,
    progress: &Regrab,
) -> Result<bool, Error> {
    let s = Session::open(http, arr).await?;
    Ok(target(&s, &progress.owner).await? == Target::Replaced)
}

/// Read-only bridge for a recorded 1.x request. It cannot return an Owner or a
/// mutation-ready Regrab, and never dispatches delete, blocklist or search.
pub async fn legacy_replacement_observed(
    http: &reqwest::Client,
    arr: &Arr,
    kind: &str,
    ids: &[i64],
    file: i64,
    path: &str,
) -> Result<bool, Error> {
    if file <= 0
        || ids.is_empty()
        || ids.len() > 256
        || ids.iter().any(|id| *id <= 0)
        || ids.iter().collect::<std::collections::HashSet<_>>().len() != ids.len()
        || !path.starts_with('/')
        || path.len() > 4096
        || path.chars().any(char::is_control)
        || path.split('/').any(|p| p == "." || p == "..")
    {
        return Err(Error::Unexpected("legacy Arr binding requires review"));
    }
    let shape = super::shape(http, arr).await?;
    let media = match (kind, shape) {
        ("movie", Some(super::Shape::Movies)) if ids.len() == 1 => Media::Movie(ids[0]),
        ("episode", Some(super::Shape::Series)) => Media::Episodes {
            ids: ids.to_vec(),
            series: 0,
            season: 0,
        },
        _ => {
            return Err(Error::Unexpected(
                "legacy Arr media kind differs from configured instance",
            ));
        }
    };
    let owner = Owner {
        media,
        imported_path: path.to_owned(),
        imported_at: None,
        recorded_file: Some(file),
        current: None,
        download_id: None,
    };
    let s = Session::open(http, arr).await?;
    Ok(target(&s, &owner).await? == Target::Replaced)
}
