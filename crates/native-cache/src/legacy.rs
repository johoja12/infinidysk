//! Read-only 1.x cache input. No legacy catalogue, manifest, marker or payload is rewritten.
//! An exclusive 1.x owner lock prevents racing its writer or eviction process.
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::io::{self, Read};
use std::os::unix::fs::{FileExt, MetadataExt, OpenOptionsExt};
use std::path::Path;
use std::sync::Arc;

use bytes::Bytes;
use nix::fcntl::{Flock, FlockArg, OFlag, openat};
use nix::sys::stat::{Mode, major, minor};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use sqlx::sqlite::{SqliteConnectOptions, SqlitePoolOptions};
use sqlx::{FromRow, SqlitePool};
use tokio::sync::OwnedSemaphorePermit;
use utoipa::ToSchema;
use uuid::Uuid;

use crate::{BLOCK, Cache, Error, Key, hex};

#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as = LegacyCacheFolder)]
pub struct Folder {
    pub id: String,
    pub path: String,
}
struct Root {
    path: String,
    file: File,
    _owner: Flock<File>,
    device: u64,
    inode: u64,
    volume: String,
}
impl Root {
    fn validate(&self) -> io::Result<()> {
        let current = std::fs::symlink_metadata(&self.path)?;
        if !current.is_dir() || current.dev() != self.device || current.ino() != self.inode {
            return Err(io::Error::other("legacy cache mount identity changed"));
        }
        let marker = leaf(&self.file, ".infinidysk-volume", self.device)?;
        let mut volume = String::new();
        marker.take(33).read_to_string(&mut volume)?;
        if volume != self.volume {
            return Err(io::Error::other("legacy cache volume marker changed"));
        }
        Ok(())
    }
    fn entry(&self, key: &str) -> io::Result<File> {
        self.validate()?;
        if key.len() != 64
            || !key
                .bytes()
                .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
        {
            return Err(io::Error::other("invalid legacy cache key"));
        }
        let mut dir = self.file.try_clone()?;
        for name in ["v1", &key[..2], key] {
            dir = File::from(openat(
                &dir,
                name,
                OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
                Mode::empty(),
            )?);
            if dir.metadata()?.dev() != self.device {
                return Err(io::Error::other("legacy cache entry crosses devices"));
            }
        }
        Ok(dir)
    }
}
fn leaf(dir: &File, name: &str, device: u64) -> io::Result<File> {
    let file = File::from(openat(
        dir,
        name,
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    )?);
    let stat = file.metadata()?;
    if !stat.is_file() || stat.nlink() != 1 || stat.dev() != device {
        return Err(io::Error::other(
            "legacy cache leaf must be a private regular file on its volume",
        ));
    }
    Ok(file)
}

pub struct Legacy {
    db: SqlitePool,
    roots: HashMap<String, Arc<Root>>,
}
#[derive(Clone, Debug, FromRow)]
pub struct Entry {
    pub key: String,
    pub folder: String,
    pub item: String,
    pub size: i64,
    pub pinned: i64,
    pub generation: Option<String>,
    pub chunk: i64,
}
#[derive(Clone, Debug, FromRow)]
pub struct Block {
    pub offset: i64,
    pub count: i64,
    pub hash: Vec<u8>,
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Manifest {
    version: u32,
    identity: Identity,
    #[serde(default)]
    chunk_size: i64,
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Identity {
    item_id: String,
    generation: String,
    length: i64,
}

impl Legacy {
    pub async fn open(catalogue: &Path, folders: &[Folder]) -> Result<Self, Error> {
        if !catalogue.is_absolute() || folders.is_empty() || folders.len() > 32 {
            return Err(Error::Invalid(
                "legacy import requires an absolute catalogue path and 1-32 folders",
            ));
        }
        let db = SqlitePoolOptions::new()
            .max_connections(1)
            .connect_with(
                SqliteConnectOptions::new()
                    .filename(catalogue)
                    .read_only(true)
                    .busy_timeout(std::time::Duration::from_secs(1)),
            )
            .await?;
        let mut roots = HashMap::new();
        for folder in folders {
            if roots.contains_key(&folder.id) {
                return Err(Error::Invalid("duplicate legacy folder id"));
            }
            let expected: Option<(String, Option<String>)> =
                sqlx::query_as("SELECT Volume,RootIdentity FROM FolderIdentity WHERE Folder=$1")
                    .bind(&folder.id)
                    .fetch_optional(&db)
                    .await?;
            let Some((volume, identity)) = expected else {
                return Err(Error::Invalid("legacy folder is not registered"));
            };
            let path = folder.path.clone();
            let root = tokio::task::spawn_blocking(move || {
                let resolved = std::fs::canonicalize(&path)?;
                if !Path::new(&path).is_absolute() || resolved != Path::new(&path) {
                    return Err(io::Error::other(
                        "legacy folder requires a canonical absolute path",
                    ));
                }
                let file = OpenOptions::new()
                    .read(true)
                    .custom_flags(nix::libc::O_DIRECTORY | nix::libc::O_NOFOLLOW)
                    .open(&path)?;
                let stat = file.metadata()?;
                let registration =
                    format!("{}:{}:{}", major(stat.dev()), minor(stat.dev()), stat.ino());
                if identity.as_deref() != Some(registration.as_str())
                    || Uuid::parse_str(&volume).is_err()
                    || volume.len() != 32
                {
                    return Err(io::Error::other(
                        "legacy folder registration does not match this mount",
                    ));
                }
                let owner = leaf(&file, ".infinidysk-owner", stat.dev())?;
                let owner =
                    Flock::lock(owner, FlockArg::LockExclusiveNonblock).map_err(|(_, error)| {
                        io::Error::other(format!("legacy cache is still owned: {error}"))
                    })?;
                let root = Root {
                    path,
                    file,
                    _owner: owner,
                    device: stat.dev(),
                    inode: stat.ino(),
                    volume,
                };
                root.validate()?;
                Ok::<_, io::Error>(root)
            })
            .await
            .map_err(|_| Error::Invalid("legacy folder validation task failed"))??;
            roots.insert(folder.id.clone(), Arc::new(root));
        }
        Ok(Self { db, roots })
    }
    pub async fn entries(&self, after: &str, limit: u32) -> Result<Vec<Entry>, Error> {
        Ok(sqlx::query_as("SELECT Key AS key,Folder AS folder,ItemId AS item,Length AS size,Pinned AS pinned,Generation AS generation,ChunkSize AS chunk FROM Entries WHERE Key>$1 ORDER BY Key LIMIT $2")
            .bind(after).bind(limit.clamp(1,64)).fetch_all(&self.db).await?)
    }
    pub async fn blocks(&self, entry: &Entry, after: i64, limit: u32) -> Result<Vec<Block>, Error> {
        Ok(sqlx::query_as("SELECT Offset AS offset,Count AS count,Hash AS hash FROM Blocks WHERE Key=$1 AND Offset>$2 ORDER BY Offset LIMIT $3")
            .bind(&entry.key).bind(after).bind(limit.clamp(1,64)).fetch_all(&self.db).await?)
    }
    /// Match both the catalogue key and independently derived source generation.
    pub fn matches(entry: &Entry, item: Uuid, generation: &str, size: u64) -> bool {
        let text = item.simple().to_string();
        let encoded = format!(
            "{}:{text}{}:{generation}:{size}",
            text.len(),
            generation.len()
        );
        entry.item == text
            && entry.generation.as_deref() == Some(generation)
            && u64::try_from(entry.size) == Ok(size)
            && entry.key == hex(&Sha256::digest(encoded.as_bytes()))
            && (entry.chunk == 0
                || (entry.chunk >= BLOCK.cast_signed()
                    && entry.chunk <= 256 * 1024 * 1024
                    && entry.chunk % BLOCK.cast_signed() == 0))
    }
    /// Validate the manifest and full block before it can be published under a Rust key.
    pub async fn copy_block(
        &self,
        cache: &Cache,
        entry: &Entry,
        key: &Key,
        block: Block,
    ) -> Result<bool, Error> {
        if Uuid::parse_str(&entry.item).ok() != Some(key.item())
            || u64::try_from(entry.size) != Ok(key.size())
        {
            return Err(Error::Invalid(
                "legacy block does not belong to this destination item",
            ));
        }
        let start =
            u64::try_from(block.offset).map_err(|_| Error::Invalid("negative legacy offset"))?;
        if start >= key.size()
            || start % BLOCK != 0
            || u64::try_from(block.count) != Ok(BLOCK.min(key.size() - start))
            || block.hash.len() != 32
        {
            return Err(Error::Invalid("invalid legacy integrity block"));
        }
        let Some(lease) = cache.lease(key.clone()) else {
            return Ok(false);
        };
        if cache.get(&lease, start / BLOCK).await.is_some() {
            if entry.pinned != 0 {
                cache.pin(key, true).await?;
            }
            return Ok(true);
        }
        let Some(permit) = cache.try_buffer() else {
            return Ok(false);
        };
        let root = Arc::clone(
            self.roots
                .get(&entry.folder)
                .ok_or(Error::Invalid("legacy entry folder is not configured"))?,
        );
        let pinned = entry.pinned != 0;
        let entry = entry.clone();
        let reading = tokio::task::spawn_blocking(move || read(&root, &entry, &block, permit));
        let (data, permit) = tokio::time::timeout(std::time::Duration::from_secs(5), reading)
            .await
            .map_err(|_| Error::Invalid("legacy cache read timed out"))?
            .map_err(|_| Error::Invalid("legacy cache read task failed"))??;
        let copied = cache
            .put_reserved(&lease, start / BLOCK, data, permit)
            .await?;
        if copied && pinned {
            cache.pin(key, true).await?;
        }
        Ok(copied)
    }
}
fn read(
    root: &Root,
    entry: &Entry,
    block: &Block,
    permit: OwnedSemaphorePermit,
) -> Result<(Bytes, OwnedSemaphorePermit), Error> {
    let dir = root.entry(&entry.key)?;
    let manifest = leaf(&dir, "manifest.json", root.device)?;
    if manifest.metadata()?.len() > 65536 {
        return Err(Error::Invalid("legacy manifest is oversized"));
    }
    let manifest: Manifest =
        serde_json::from_reader(manifest).map_err(|_| Error::Invalid("invalid legacy manifest"))?;
    if manifest.identity.item_id != entry.item
        || Some(&manifest.identity.generation) != entry.generation.as_ref()
        || manifest.identity.length != entry.size
        || manifest.chunk_size != entry.chunk
        || (entry.chunk == 0 && manifest.version != 1)
        || (entry.chunk != 0 && manifest.version != 2)
    {
        return Err(Error::Invalid(
            "legacy manifest disagrees with its catalogue",
        ));
    }
    let start = block.offset.cast_unsigned();
    let name = if entry.chunk == 0 {
        "content.data".to_owned()
    } else {
        format!("chunk-{:016x}.data", start / entry.chunk.cast_unsigned())
    };
    let file = leaf(&dir, &name, root.device)?;
    let mut bytes = vec![
        0;
        usize::try_from(block.count)
            .map_err(|_| Error::Invalid("invalid legacy block length"))?
    ];
    file.read_exact_at(
        &mut bytes,
        if entry.chunk == 0 {
            start
        } else {
            start % entry.chunk.cast_unsigned()
        },
    )?;
    if Sha256::digest(&bytes).as_slice() != block.hash {
        return Err(Error::Invalid("legacy block checksum differs"));
    }
    root.validate()?;
    Ok((Bytes::from(bytes), permit))
}
