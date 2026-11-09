//! Source-bound recovery records. Recovery may lose an interrupted tail, never
//! infer verified coverage from a chunk's filename or allocated size.
use super::{BLOCK, Folder, Key, hex};
use nix::fcntl::{Flock, FlockArg, OFlag, openat};
use nix::sys::stat::Mode;
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use std::fs::File;
use std::io::{self, Read as _, Write as _};
use std::os::unix::fs::{FileExt as _, MetadataExt as _};
use uuid::Uuid;

pub(super) const SUFFIX: &str = ".recovery-v1.jsonl";
pub(super) const MAX_RECORD: usize = 4096;
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Record {
    pub version: u8,
    pub folder: Uuid,
    pub marker: String,
    pub device: u64,
    pub inode: u64,
    pub item: Uuid,
    pub revision: String,
    pub size: u64,
    pub chunk: u64,
    pub block: u64,
    pub len: u64,
    pub hash: String,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Envelope {
    data: Record,
    checksum: String,
}
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "invalid cache recovery record")
}
impl Record {
    pub(super) fn new(
        folder: &Folder,
        key: &Key,
        chunk: u64,
        block: u64,
        len: u64,
        hash: String,
    ) -> Self {
        Self {
            version: 1,
            folder: folder.settings.id,
            marker: folder.marker.clone(),
            device: folder.device,
            inode: folder.inode,
            item: key.item,
            revision: key.revision.clone(),
            size: key.size,
            chunk,
            block,
            len,
            hash,
        }
    }
    pub(super) fn key(&self) -> Key {
        Key {
            item: self.item,
            revision: self.revision.clone(),
            size: self.size,
        }
    }
    pub(super) fn validate(&self) -> io::Result<()> {
        let start = self.block.checked_mul(BLOCK).ok_or_else(invalid)?;
        if self.version != 1
            || self.folder.is_nil()
            || self.item.is_nil()
            || Uuid::parse_str(&self.marker).is_err()
            || self.marker.len() != 36
            || self.revision.len() != 64
            || !self.revision.bytes().all(|b| b.is_ascii_hexdigit())
            || self.hash.len() != 64
            || !self.hash.bytes().all(|b| b.is_ascii_hexdigit())
            || self.size == 0
            || self.size > i64::MAX.cast_unsigned()
            || start >= self.size
            || self.len != BLOCK.min(self.size - start)
            || !(BLOCK..=256 * 1_000_000u64 + BLOCK).contains(&self.chunk)
            || !self.chunk.is_multiple_of(BLOCK)
        {
            return Err(invalid());
        }
        Ok(())
    }
    pub(super) fn bound(&self, folder: &Folder) -> io::Result<()> {
        self.validate()?;
        if self.folder != folder.settings.id
            || self.marker != folder.marker
            || self.device != folder.device
            || self.inode != folder.inode
        {
            return Err(invalid());
        }
        folder.validate()
    }
    fn encode(&self) -> io::Result<Vec<u8>> {
        self.validate()?;
        let payload = serde_json::to_vec(self).map_err(|_| invalid())?;
        let mut framed = serde_json::to_vec(&Envelope {
            data: self.clone(),
            checksum: hex(&Sha256::digest(payload)),
        })
        .map_err(|_| invalid())?;
        framed.push(b'\n');
        if framed.len() > MAX_RECORD {
            return Err(invalid());
        }
        Ok(framed)
    }
}
pub(super) fn decode(line: &[u8]) -> io::Result<Record> {
    if line.len() > MAX_RECORD {
        return Err(invalid());
    }
    let envelope: Envelope = serde_json::from_slice(line).map_err(|_| invalid())?;
    envelope.data.validate()?;
    let payload = serde_json::to_vec(&envelope.data).map_err(|_| invalid())?;
    if envelope.checksum != hex(&Sha256::digest(payload)) {
        return Err(invalid());
    }
    Ok(envelope.data)
}
fn checked_file(folder: &Folder, name: &str, flags: OFlag) -> io::Result<File> {
    folder.validate()?;
    let file = File::from(openat(
        &folder.root,
        name,
        flags | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::S_IRUSR | Mode::S_IWUSR,
    )?);
    let metadata = file.metadata()?;
    if !metadata.is_file() || metadata.nlink() != 1 {
        return Err(invalid());
    }
    Ok(file)
}
/// Called after complete payload sync, before catalogue publication. A complete
/// frame permits later checksum verification; an interrupted frame gives no coverage.
/// Append locks cover only the small journal write, not parallel payload transfers.
pub(super) fn append(folder: &Folder, record: &Record) -> io::Result<()> {
    record.bound(folder)?;
    let name = format!("{}{SUFFIX}", record.key().id());
    let file = checked_file(
        folder,
        &name,
        OFlag::O_RDWR | OFlag::O_CREAT | OFlag::O_APPEND,
    )?;
    let mut file =
        Flock::lock(file, FlockArg::LockExclusive).map_err(|(_, e)| io::Error::from(e))?;
    let length = file.metadata()?.len();
    if length > 0 {
        let count = usize::try_from(length.min(MAX_RECORD as u64)).map_err(|_| invalid())?;
        let mut tail = vec![0; count];
        file.read_exact_at(&mut tail, length - count as u64)?;
        let end = tail.iter().rposition(|b| *b == b'\n').ok_or_else(invalid)?;
        if end + 1 < tail.len() {
            file.set_len(length - count as u64 + end as u64 + 1)?;
        }
    }
    file.write_all(&record.encode()?)?;
    // Recovery is conservative if an OS crash loses recent journal buffers. Payload
    // bytes are already synced, and every recovered frame must verify them again.
    Ok(())
}
/// One bounded frame at an exact journal offset. Partial tails are not records.
pub(super) fn read_at(
    folder: &Folder,
    key: &Key,
    offset: u64,
) -> io::Result<Option<(Record, u64)>> {
    let name = format!("{}{SUFFIX}", key.id());
    let file = checked_file(folder, &name, OFlag::O_RDONLY)?;
    let length = file.metadata()?.len();
    if offset >= length {
        return Ok(None);
    }
    let mut bytes =
        vec![0; usize::try_from((length - offset).min(MAX_RECORD as u64)).map_err(|_| invalid())?];
    file.read_exact_at(&mut bytes, offset)?;
    let Some(end) = bytes.iter().position(|b| *b == b'\n') else {
        return if length - offset < MAX_RECORD as u64 {
            Ok(None)
        } else {
            Err(invalid())
        };
    };
    let record = decode(&bytes[..end])?;
    record.bound(folder)?;
    if record.key() != *key {
        return Err(invalid());
    }
    Ok(Some((record, offset + end as u64 + 1)))
}
#[cfg(test)]
mod tests {
    use super::*;
    fn record() -> Record {
        let key = Key::new(Uuid::new_v4(), b"fixture-source", BLOCK + 12);
        Record {
            version: 1,
            folder: Uuid::new_v4(),
            marker: Uuid::new_v4().to_string(),
            device: 1,
            inode: 2,
            item: key.item,
            revision: key.revision,
            size: key.size,
            chunk: 16 * BLOCK,
            block: 1,
            len: 12,
            hash: hex(&Sha256::digest([7; 12])),
        }
    }
    #[test]
    fn recovery_frames_bind_geometry_generation_and_checksum() {
        let record = record();
        let bytes = record.encode().unwrap();
        assert_eq!(decode(&bytes).unwrap(), record);
        let mut frame: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
        frame["data"]["revision"] = serde_json::json!("0".repeat(64));
        assert!(decode(&serde_json::to_vec(&frame).unwrap()).is_err());
        let mut invalid = record.clone();
        invalid.len = 11;
        assert!(invalid.encode().is_err());
        invalid = record;
        invalid.block = u64::MAX;
        assert!(invalid.encode().is_err());
    }
}

fn first(root: &File, name: &str) -> io::Result<Record> {
    let file = File::from(openat(
        root,
        name,
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    )?);
    if !file.metadata()?.is_file() || file.metadata()?.nlink() != 1 {
        return Err(invalid());
    }
    let mut bytes = vec![
        0;
        usize::try_from(file.metadata()?.len().min(MAX_RECORD as u64))
            .map_err(|_| invalid())?
    ];
    file.read_exact_at(&mut bytes, 0)?;
    let end = bytes.iter().position(|b| *b == b'\n').ok_or_else(invalid)?;
    decode(&bytes[..end])
}
/// Explicit recovery admission only. Normal startup never adopts unknown payloads.
pub(super) fn ownership(root: &File, settings: &super::FolderSettings) -> io::Result<String> {
    let metadata = root.metadata()?;
    let owner = File::from(openat(
        root,
        super::OWNER,
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    )?);
    if !owner.metadata()?.is_file() || owner.metadata()?.nlink() != 1 {
        return Err(invalid());
    }
    let mut marker = String::new();
    owner.take(100).read_to_string(&mut marker)?;
    if Uuid::parse_str(&marker).is_err() {
        return Err(invalid());
    }
    for (count, entry) in std::fs::read_dir(&settings.path)?.enumerate() {
        if count >= 100_000 {
            return Err(io::Error::other("cache recovery folder exceeds scan bound"));
        }
        let name = entry?.file_name();
        let Some(name) = name.to_str().filter(|name| name.ends_with(SUFFIX)) else {
            continue;
        };
        let Ok(record) = first(root, name) else {
            continue;
        };
        if record.folder == settings.id
            && record.marker == marker
            && record.device == metadata.dev()
            && record.inode == metadata.ino()
            && name == format!("{}{SUFFIX}", record.key().id())
        {
            return Ok(marker);
        }
    }
    Err(io::Error::other(
        "cache recovery requires a matching source-bound manifest",
    ))
}
#[derive(Clone, Debug, Serialize, Deserialize, utoipa::ToSchema)]
#[schema(as=NativeCacheRecoveryCandidate)]
pub struct Candidate {
    pub item: Uuid,
    pub revision: String,
    pub size_bytes: u64,
    pub key: String,
}
#[derive(Serialize, utoipa::ToSchema)]
#[schema(as=NativeCacheRecoveryPage)]
pub struct Page {
    pub entries: Vec<Candidate>,
    pub after: Option<String>,
    pub rejected: u32,
}
#[derive(Serialize, utoipa::ToSchema)]
#[schema(as=NativeCacheRecoveryResult)]
pub struct Report {
    pub next_offset: Option<u64>,
    pub verified_blocks: u32,
    pub verified_bytes: u64,
}
impl super::Cache {
    pub fn recovery_mode(&self) -> bool {
        self.settings.recovery_mode
    }
    pub async fn recovery_candidates(
        &self,
        folder_id: Uuid,
        after: Option<&str>,
    ) -> Result<Page, super::Error> {
        if !self.settings.recovery_mode {
            return Err(super::Error::Invalid(
                "restart in native cache recovery mode before scanning",
            ));
        }
        let folder = self
            .folders
            .iter()
            .find(|f| f.settings.id == folder_id)
            .cloned()
            .ok_or(super::Error::Invalid("unknown cache folder"))?;
        let permit = self
            .try_buffer()
            .ok_or(super::Error::Invalid("cache recovery is busy"))?;
        let after = after.unwrap_or_default().to_owned();
        let mut observation = self.io.begin(super::traffic::Kind::Read);
        let work = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            observation.start();
            folder.validate()?;
            let mut names = std::collections::BTreeSet::new();
            for (count, entry) in std::fs::read_dir(&folder.settings.path)?.enumerate() {
                if count >= 100_000 {
                    return Err(io::Error::other("cache recovery folder exceeds scan bound"));
                }
                let name = entry?.file_name();
                let Some(name) = name.to_str() else {
                    continue;
                };
                if name.ends_with(SUFFIX) && name > after.as_str() {
                    names.insert(name.to_owned());
                    if names.len() > 51 {
                        names.pop_last();
                    }
                }
            }
            let more = names.len() > 50;
            if more {
                names.pop_last();
            }
            let cursor = more.then(|| names.last().cloned()).flatten();
            let mut entries = Vec::new();
            let mut rejected = 0;
            for name in names {
                match first(&folder.root, &name).and_then(|record| {
                    record.bound(&folder)?;
                    if name != format!("{}{SUFFIX}", record.key().id()) {
                        return Err(invalid());
                    }
                    Ok(record)
                }) {
                    Ok(record) => entries.push(Candidate {
                        item: record.item,
                        revision: record.revision.clone(),
                        size_bytes: record.size,
                        key: record.key().id(),
                    }),
                    Err(_) => rejected += 1,
                }
            }
            folder.validate()?;
            observation.success(0);
            Ok::<_, io::Error>(Page {
                entries,
                after: cursor,
                rejected,
            })
        });
        match tokio::time::timeout(self.deadline(), work).await {
            Ok(Ok(result)) => Ok(result?),
            _ => Err(super::Error::Invalid(
                "cache recovery scan exceeded its storage deadline",
            )),
        }
    }
    /// Caller must obtain `key` from current source metadata and the trusted repair
    /// index. This restores only that revision; no record authorizes source mutation.
    pub async fn recover_blocks(
        &self,
        folder_id: Uuid,
        key: &Key,
        offset: u64,
    ) -> Result<Report, super::Error> {
        if !self.settings.recovery_mode {
            return Err(super::Error::Invalid(
                "restart in native cache recovery mode before recovery",
            ));
        }
        let folder = self
            .folders
            .iter()
            .find(|f| f.settings.id == folder_id)
            .cloned()
            .ok_or(super::Error::Invalid("unknown cache folder"))?;
        let lease = self
            .lease(key.clone())
            .ok_or(super::Error::Invalid("source cannot enter cache recovery"))?;
        let guard = self
            .entry_lock(key, None)
            .try_lock_owned()
            .map_err(|_| super::Error::Invalid("cache entry recovery is busy"))?;
        let permit = self
            .try_buffer()
            .ok_or(super::Error::Invalid("cache recovery is busy"))?;
        let key = key.clone();
        let reading_key = key.clone();
        let mut observation = self.io.begin(super::traffic::Kind::Read);
        let work = tokio::task::spawn_blocking(move || {
            let (_permit, _lease) = (permit, lease);
            observation.start();
            let mut next = offset;
            let mut records = Vec::new();
            let mut bytes = 0;
            for _ in 0..4 {
                let Some((record, end)) = read_at(&folder, &reading_key, next)? else {
                    break;
                };
                let start = record.block * BLOCK;
                let name = format!("{}-{}.data", reading_key.id(), start / record.chunk);
                let file = folder.file(&name, false)?;
                let mut hash = Sha256::new();
                let mut buffer = vec![0; 64 * 1024];
                let mut at = 0;
                while at < record.len {
                    let n = usize::try_from((record.len - at).min(buffer.len() as u64))
                        .map_err(|_| invalid())?;
                    file.read_exact_at(&mut buffer[..n], start % record.chunk + at)?;
                    hash.update(&buffer[..n]);
                    at += n as u64;
                }
                if hex(&hash.finalize()) != record.hash {
                    return Err(invalid());
                }
                bytes += record.len;
                records.push(record);
                next = end;
            }
            let complete = read_at(&folder, &reading_key, next)?.is_none();
            folder.validate()?;
            observation.success(bytes);
            Ok::<_, io::Error>((records, next, complete, guard))
        });
        let (records, next, complete, _guard) =
            match tokio::time::timeout(self.deadline(), work).await {
                Ok(Ok(result)) => result?,
                _ => {
                    return Err(super::Error::Invalid(
                        "cache recovery verification exceeded its storage deadline",
                    ));
                }
            };
        if let Some(first) = records.first() {
            let chunk = first.chunk;
            if records.iter().any(|record| record.chunk != chunk) {
                return Err(super::Error::Invalid("cache recovery geometry changed"));
            }
            let mut tx = self.db.begin().await?;
            sqlx::query("INSERT INTO entries(key,item,revision,size,folder,chunk,used,pinned) VALUES($1,$2,$3,$4,$5,$6,$7,1) ON CONFLICT(key) DO NOTHING")
                .bind(key.id()).bind(key.item.to_string()).bind(&key.revision).bind(key.size.cast_signed()).bind(folder_id.to_string()).bind(chunk.cast_signed()).bind(super::now()).execute(&mut *tx).await?;
            let existing: (String, i64) =
                sqlx::query_as("SELECT folder,chunk FROM entries WHERE key=$1")
                    .bind(key.id())
                    .fetch_one(&mut *tx)
                    .await?;
            if existing != (folder_id.to_string(), chunk.cast_signed()) {
                return Err(super::Error::Invalid(
                    "existing cache placement differs from recovery records",
                ));
            }
            for record in &records {
                sqlx::query("INSERT INTO blocks(key,block,len,hash,valid) VALUES($1,$2,$3,$4,1) ON CONFLICT(key,block) DO UPDATE SET len=excluded.len,hash=excluded.hash,valid=1")
                    .bind(key.id()).bind(record.block.cast_signed()).bind(record.len.cast_signed()).bind(&record.hash).execute(&mut *tx).await?;
            }
            tx.commit().await?;
        }
        Ok(Report {
            next_offset: (!complete).then_some(next),
            verified_blocks: u32::try_from(records.len())
                .expect("a recovery page has at most four records"),
            verified_bytes: records.iter().map(|r| r.len).sum(),
        })
    }
}

#[cfg(test)]
mod recovery_tests {
    use super::*;
    use crate::{Cache, FolderSettings, Settings};
    use bytes::Bytes;
    struct Temp(std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    async fn written() -> (Temp, Settings, Key) {
        written_size(BLOCK + 12).await
    }
    async fn written_size(size: u64) -> (Temp, Settings, Key) {
        let root = std::env::temp_dir().join(format!("id-native-recovery-{}", Uuid::new_v4()));
        std::fs::create_dir_all(root.join("payload")).unwrap();
        let settings = Settings {
            enabled: true,
            min_file_mb: 0,
            // These cases verify recovery state, not throughput. Debug hashing on
            // shared CI can exceed the production one-second storage deadline.
            io_timeout_ms: 10_000,
            metadata_path: root.join("metadata").to_string_lossy().into_owned(),
            folders: vec![FolderSettings {
                id: Uuid::new_v4(),
                path: root.join("payload").to_string_lossy().into_owned(),
                min_free_bytes: 0,
                ..Default::default()
            }],
            ..Default::default()
        };
        let key = Key::new(Uuid::new_v4(), b"fixture-generation", size);
        let cache = Cache::open(settings.clone()).await.unwrap();
        let lease = cache.lease(key.clone()).unwrap();
        for block in 0..size.div_ceil(BLOCK) {
            let len = BLOCK.min(size - block * BLOCK);
            let value = if len == BLOCK { 7 } else { 9 };
            assert!(
                cache
                    .put(
                        &lease,
                        block,
                        Bytes::from(vec![value; usize::try_from(len).unwrap()])
                    )
                    .await
                    .unwrap()
            );
        }
        drop(lease);
        cache.db.close().await;
        drop(cache);
        std::fs::rename(&settings.metadata_path, root.join("lost-metadata")).unwrap();
        (Temp(root), settings, key)
    }
    #[tokio::test]
    async fn explicit_reconstruction_restores_only_verified_generation_and_keeps_payloads_read_only()
     {
        let (_temp, mut settings, key) = written().await;
        assert!(
            Cache::open(settings.clone()).await.is_err(),
            "ordinary startup never adopts unknown payloads"
        );
        settings.recovery_mode = true;
        let cache = Cache::open(settings.clone()).await.unwrap();
        let page = cache
            .recovery_candidates(settings.folders[0].id, None)
            .await
            .unwrap();
        assert_eq!(page.entries.len(), 1);
        assert_eq!(page.entries[0].item, key.item());
        let report = cache
            .recover_blocks(settings.folders[0].id, &key, 0)
            .await
            .unwrap();
        assert_eq!(report.verified_blocks, 2);
        assert_eq!(report.verified_bytes, BLOCK + 12);
        assert_eq!(report.next_offset, None);
        let entries = cache.entries(0, 10).await.unwrap();
        assert!(
            entries[0].pinned,
            "restored copies stay protected until reviewed"
        );
        let lease = cache.lease(key.clone()).unwrap();
        assert_eq!(
            cache.get(&lease, 1).await.unwrap(),
            Bytes::from(vec![9; 12])
        );
        assert!(
            !cache
                .put(&lease, 1, Bytes::from(vec![9; 12]))
                .await
                .unwrap()
        );
        drop(lease);
        assert!(!cache.evict(&key, "fixture").await.unwrap());
        cache.db.close().await;
        drop(cache);
        settings.recovery_mode = false;
        let cache = Cache::open(settings).await.unwrap();
        let lease = cache.lease(key).unwrap();
        assert_eq!(
            cache.get(&lease, 0).await.unwrap(),
            Bytes::from(vec![7; crate::BLOCK_BYTES])
        );
        drop(lease);
        cache.db.close().await;
    }
    #[tokio::test]
    async fn corrupt_payload_and_changed_generation_cannot_rebuild_coverage() {
        let (_temp, mut settings, key) = written().await;
        settings.recovery_mode = true;
        let data = std::fs::read_dir(&settings.folders[0].path)
            .unwrap()
            .map(|e| e.unwrap().path())
            .find(|p| p.extension().is_some_and(|e| e == "data"))
            .unwrap();
        let file = std::fs::OpenOptions::new().write(true).open(data).unwrap();
        file.write_all_at(&[0], 0).unwrap();
        drop(file);
        let cache = Cache::open(settings.clone()).await.unwrap();
        assert!(
            cache
                .recover_blocks(settings.folders[0].id, &key, 0)
                .await
                .is_err()
        );
        assert_eq!(cache.coverage(&key).await.unwrap().len(), 0);
        let changed = Key::new(key.item(), b"changed-generation", key.size());
        assert!(
            cache
                .recover_blocks(settings.folders[0].id, &changed, 0)
                .await
                .is_err()
        );
        assert_eq!(cache.coverage(&changed).await.unwrap().len(), 0);
        cache.db.close().await;
    }
    #[tokio::test]
    async fn an_interrupted_journal_tail_does_not_invent_or_hide_complete_records() {
        let (_temp, mut settings, key) = written().await;
        let path =
            std::path::Path::new(&settings.folders[0].path).join(format!("{}{SUFFIX}", key.id()));
        let mut file = std::fs::OpenOptions::new().append(true).open(path).unwrap();
        file.write_all(b"{\"data\":").unwrap();
        drop(file);
        settings.recovery_mode = true;
        let cache = Cache::open(settings.clone()).await.unwrap();
        let report = cache
            .recover_blocks(settings.folders[0].id, &key, 0)
            .await
            .unwrap();
        assert_eq!(report.verified_bytes, BLOCK + 12);
        cache.db.close().await;
    }
    #[tokio::test]
    async fn bounded_pages_preserve_verified_coverage_and_resume_after_restart() {
        let (_temp, mut settings, key) = written_size(7 * BLOCK + 12).await;
        settings.recovery_mode = true;
        let cache = Cache::open(settings.clone()).await.unwrap();
        let first = cache
            .recover_blocks(settings.folders[0].id, &key, 0)
            .await
            .unwrap();
        assert_eq!(first.verified_blocks, 4);
        assert_eq!(first.verified_bytes, 4 * BLOCK);
        assert_eq!(cache.coverage(&key).await.unwrap()[0].end, 4 * BLOCK);
        assert_eq!(cache.verify_complete(&key).await.unwrap(), None);
        assert_eq!(cache.verify_and_protect(&key).await.unwrap(), None);
        cache.db.close().await;
        drop(cache);
        let cache = Cache::open(settings.clone()).await.unwrap();
        let second = cache
            .recover_blocks(settings.folders[0].id, &key, first.next_offset.unwrap())
            .await
            .unwrap();
        assert_eq!(second.verified_blocks, 4);
        assert_eq!(second.next_offset, None);
        assert_eq!(cache.coverage(&key).await.unwrap()[0].end, key.size());
        assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(true));
        cache.db.close().await;
    }
}
