//! Bounded filesystem slices over a durable directory queue. Only an entirely
//! finished generation can replace the public index.
use super::{Known, digest};
use crate::library::{Found, classify_checked};
use futures_util::{StreamExt as _, TryStreamExt as _};
use infinidysk_core::media;
use infinidysk_db::{
    Db,
    catalogue::Link,
    library_scan::{Batch, Directory, Scan},
};
use std::{
    fs, io,
    os::unix::fs::MetadataExt as _,
    path::{Path, PathBuf},
    time::{Duration, Instant},
};
use tokio_util::sync::CancellationToken;
const BATCH: usize = 256;
const WORK: usize = 5000;
#[derive(Clone)]
pub(super) struct Identity {
    pub device: String,
    pub inode: String,
    pub physical: PathBuf,
}
pub(super) enum Step {
    Pending,
    Complete,
}
pub(super) fn identity(root: &Path, known: &Known) -> io::Result<Identity> {
    let meta = fs::symlink_metadata(root)?;
    let physical = fs::canonicalize(root)?;
    if !meta.is_dir()
        || known
            .skip
            .iter()
            .any(|skip| root.starts_with(skip) || physical.starts_with(skip))
    {
        return Err(io::Error::other(
            "library root is not an available unprotected directory",
        ));
    }
    Ok(Identity {
        device: meta.dev().to_string(),
        inode: meta.ino().to_string(),
        physical,
    })
}
fn check_identity(root: &Path, known: &Known, run: &Scan) -> anyhow::Result<Identity> {
    let current = identity(root, known)?;
    anyhow::ensure!(
        current.device == run.device && current.inode == run.inode,
        "library root identity changed"
    );
    Ok(current)
}
async fn link(db: &Db, root: &Path, found: Found) -> anyhow::Result<Link> {
    let path = found
        .path
        .to_str()
        .ok_or_else(|| anyhow::anyhow!("non-UTF8 library pointer"))?
        .to_owned();
    let relative = found
        .path
        .strip_prefix(root)?
        .to_str()
        .ok_or_else(|| anyhow::anyhow!("non-UTF8 relative pointer"))?
        .to_owned();
    let video = media::is_video(&path)
        || db
            .item(found.item)
            .await?
            .is_some_and(|item| media::is_video(&item.name));
    Ok(Link {
        root: root
            .to_str()
            .ok_or_else(|| anyhow::anyhow!("non-UTF8 library root"))?
            .to_owned(),
        path,
        group_name: relative.split('/').next().unwrap_or_default().to_owned(),
        relative_path: relative,
        item_id: found.item.0,
        pointer_hash: digest(found.pointer.text().as_bytes()),
        link_kind: if found.pointer.is_link() { 1 } else { 2 },
        video: i16::from(video),
    })
}
struct Walker<'a> {
    db: &'a Db,
    root: &'a Path,
    known: &'a Known,
    run: &'a Scan,
    cancel: &'a CancellationToken,
}
impl Walker<'_> {
    fn checkpoint(
        &self,
        directory: &Directory,
        mut paths: Vec<PathBuf>,
        complete: bool,
    ) -> anyhow::Result<usize> {
        let Self {
            db,
            root,
            known,
            run,
            cancel,
        } = self;
        paths.sort();
        paths.dedup();
        let names = paths
            .iter()
            .map(|path| {
                path.to_str()
                    .map(str::to_owned)
                    .ok_or_else(|| anyhow::anyhow!("non-UTF8 library entry"))
            })
            .collect::<anyhow::Result<Vec<_>>>()?;
        let runtime = tokio::runtime::Handle::current();
        let seen = runtime.block_on(db.catalogue_scan_seen(&run.root, &run.worker, &names))?;
        let mut batch = Batch {
            directory: directory.path.clone(),
            seen: vec![],
            directories: vec![],
            links: vec![],
            complete,
        };
        let mut found = Vec::new();
        for (path, name) in paths.into_iter().zip(names) {
            if cancel.is_cancelled() {
                return Ok(0);
            }
            if seen.contains(&name) {
                continue;
            }
            let metadata = fs::symlink_metadata(&path)?;
            batch.seen.push(name.clone());
            if metadata.is_dir() {
                let physical = fs::canonicalize(&path)?;
                if !known
                    .skip
                    .iter()
                    .any(|skip| path.starts_with(skip) || physical.starts_with(skip))
                {
                    anyhow::ensure!(
                        directory.depth < 64,
                        "library directory depth exceeds scan bound"
                    );
                    batch.directories.push(Directory {
                        path: name,
                        depth: directory.depth + 1,
                    });
                }
            } else if let Some(pointer) = classify_checked(&path, &metadata, known)? {
                found.push(pointer);
            }
        }
        batch.links = runtime.block_on(
            futures_util::stream::iter(found.into_iter().map(|found| link(db, root, found)))
                .buffered(16)
                .try_collect(),
        )?;
        if cancel.is_cancelled() {
            return Ok(0);
        }
        let work = batch.seen.len() + usize::from(complete);
        runtime.block_on(db.catalogue_scan_checkpoint(&run.root, &run.worker, &batch))?;
        Ok(work)
    }
}
pub(super) fn slice(
    db: &Db,
    root: &Path,
    known: &Known,
    run: &Scan,
    cancel: &CancellationToken,
) -> anyhow::Result<Step> {
    let origin = check_identity(root, known, run)?;
    let walker = Walker {
        db,
        root,
        known,
        run,
        cancel,
    };
    let runtime = tokio::runtime::Handle::current();
    let started = Instant::now();
    let mut worked = 0;
    loop {
        if cancel.is_cancelled() {
            return Ok(Step::Pending);
        }
        let Some(directory) =
            runtime.block_on(db.catalogue_scan_directory(&run.root, &run.worker))?
        else {
            check_identity(root, known, run)?;
            return Ok(Step::Complete);
        };
        let path = Path::new(&directory.path);
        let metadata = fs::symlink_metadata(path)?;
        let physical = fs::canonicalize(path)?;
        anyhow::ensure!(
            metadata.is_dir()
                && physical.starts_with(&origin.physical)
                && !known
                    .skip
                    .iter()
                    .any(|skip| path.starts_with(skip) || physical.starts_with(skip)),
            "library directory identity or protection changed"
        );
        let mut pending = Vec::with_capacity(BATCH);
        for entry in fs::read_dir(path)? {
            if cancel.is_cancelled() {
                return Ok(Step::Pending);
            }
            pending.push(entry?.path());
            if pending.len() == BATCH {
                let count = walker.checkpoint(&directory, std::mem::take(&mut pending), false)?;
                worked += count;
                if worked >= WORK || count > 0 && started.elapsed() >= Duration::from_secs(15) {
                    check_identity(root, known, run)?;
                    return Ok(Step::Pending);
                }
            }
        }
        worked += walker.checkpoint(&directory, pending, true)?;
        if worked >= WORK || started.elapsed() >= Duration::from_secs(15) {
            check_identity(root, known, run)?;
            return Ok(Step::Pending);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_core::ItemId;
    use uuid::Uuid;
    struct Temp(PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    #[tokio::test]
    async fn staged_walk_resumes_without_exposing_partial_links() {
        let path = std::env::temp_dir().join(format!("id-staged-walk-{}", Uuid::new_v4()));
        fs::create_dir_all(path.join("Collection")).unwrap();
        let _temp = Temp(path.clone());
        let id = ItemId::new();
        for n in 0..6001 {
            std::os::unix::fs::symlink(
                format!("/fixture-mount/{}", id.ids_path()),
                path.join(format!("Collection/file-{n:05}.mkv")),
            )
            .unwrap();
        }
        let db = Db::connect("sqlite::memory:").await.unwrap();
        let root = path.to_str().unwrap();
        let old = Link {
            root: root.into(),
            path: format!("{root}/Collection/previous.mkv"),
            relative_path: "Collection/previous.mkv".into(),
            group_name: "Collection".into(),
            item_id: id.0,
            pointer_hash: "previous".into(),
            link_kind: 1,
            video: 1,
        };
        db.catalogue_replace(root, "previous", std::slice::from_ref(&old), 1, 0)
            .await
            .unwrap();
        let known = Known {
            mount_dirs: vec!["/fixture-mount".into()],
            ..Default::default()
        };
        let physical = identity(&path, &known).unwrap();
        let run = db
            .catalogue_scan_claim(
                root,
                "current",
                Uuid::new_v4(),
                (&physical.device, &physical.inode),
                0,
            )
            .await
            .unwrap();
        let working_db = db.clone();
        let working_path = path.clone();
        let working_known = known.clone();
        let first = tokio::task::spawn_blocking(move || {
            slice(
                &working_db,
                &working_path,
                &working_known,
                &run,
                &CancellationToken::new(),
            )
        })
        .await
        .unwrap()
        .unwrap();
        assert!(matches!(first, Step::Pending));
        assert_eq!(
            db.catalogue_links(Some(root), None, None, false, "", 100)
                .await
                .unwrap(),
            vec![old]
        );
        let progress = db.catalogue_scans().await.unwrap();
        assert!(progress[0].visited >= 5000);
        let run = db
            .catalogue_scan_claim(
                root,
                "current",
                Uuid::new_v4(),
                (&physical.device, &physical.inode),
                0,
            )
            .await
            .unwrap();
        let worker = run.worker.clone();
        let working_db = db.clone();
        let working_path = path.clone();
        let working_known = known.clone();
        let last = tokio::task::spawn_blocking(move || {
            slice(
                &working_db,
                &working_path,
                &working_known,
                &run,
                &CancellationToken::new(),
            )
        })
        .await
        .unwrap()
        .unwrap();
        assert!(matches!(last, Step::Complete));
        assert!(
            db.catalogue_scan_publish(root, "current", &worker, 2)
                .await
                .unwrap()
        );
        assert_eq!(
            db.catalogue_groups(root, "", 10).await.unwrap()[0].files,
            6001
        );
        db.close().await;
    }
    #[test]
    fn pointer_io_failure_is_distinct_from_an_unsupported_pointer() {
        let path = std::env::temp_dir().join(format!("id-pointer-{}.strm", Uuid::new_v4()));
        fs::write(&path, b"not-a-pointer").unwrap();
        let metadata = fs::metadata(&path).unwrap();
        assert!(
            classify_checked(&path, &metadata, &Known::default())
                .unwrap()
                .is_none()
        );
        fs::remove_file(&path).unwrap();
        assert!(classify_checked(&path, &metadata, &Known::default()).is_err());
    }
}
