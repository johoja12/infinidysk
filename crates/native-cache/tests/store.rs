//! Fault and restart checks use private temporary directories; no provider is used.
#![allow(clippy::unwrap_used)] // Fixture setup failures are assertions.
use bytes::Bytes;
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, FolderSettings, Key, Settings};
use std::os::unix::fs::FileExt;
use std::path::PathBuf;
use std::sync::Arc;
use uuid::Uuid;

struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let p = std::env::temp_dir().join(format!("id-native-test-{}", Uuid::new_v4()));
        std::fs::create_dir_all(p.join("data")).unwrap();
        Self(p)
    }
    fn settings(&self) -> Settings {
        Settings {
            enabled: true,
            metadata_path: self.0.join("metadata").to_str().unwrap().to_owned(),
            min_file_mb: 0,
            folders: vec![FolderSettings {
                id: Uuid::from_u128(1),
                path: self.0.join("data").to_str().unwrap().to_owned(),
                quota_bytes: 100 * BLOCK,
                min_free_bytes: 0,
                ..FolderSettings::default()
            }],
            ..Settings::default()
        }
    }
    fn payload(&self, key: &Key, chunk: u64) -> PathBuf {
        self.0.join("data").join(format!(
            "{}-{}-{chunk}.data",
            key.item().simple(),
            key.revision()
        ))
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn key(size: u64) -> Key {
    Key::new(Uuid::from_u128(2), b"metadata", size)
}
fn data(value: u8) -> Bytes {
    Bytes::from(vec![value; BLOCK_BYTES])
}

#[tokio::test]
async fn committed_blocks_survive_restart_and_partial_blocks_are_not_coverage() {
    let t = Temp::new();
    let settings = t.settings();
    let k = key(2 * BLOCK + 12);
    let cache = Cache::open(settings.clone()).await.unwrap();
    let lease = cache.lease(k.clone()).unwrap();
    assert!(
        !cache
            .put(&lease, 0, Bytes::from_static(b"short"))
            .await
            .unwrap()
    );
    assert!(cache.put(&lease, 0, data(7)).await.unwrap());
    assert!(
        cache
            .put(&lease, 2, Bytes::from(vec![9; 12]))
            .await
            .unwrap()
    );
    let ranges = cache.coverage(&k).await.unwrap();
    assert_eq!(ranges.len(), 2);
    assert_eq!(ranges[0].end, BLOCK);
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(7));
    drop(lease);
    drop(cache);
    let cache = Cache::open(settings).await.unwrap();
    let lease = cache.lease(k).unwrap();
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(7));
    assert_eq!(
        cache.get(&lease, 2).await.unwrap(),
        Bytes::from(vec![9; 12])
    );
    assert_eq!(cache.stats().await.unwrap().bytes, BLOCK + 12);
}

#[tokio::test]
async fn corruption_is_a_miss_and_a_later_verified_fill_restores_coverage() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    std::fs::write(t.payload(&k, 0), b"torn").unwrap();
    assert!(cache.get(&lease, 0).await.is_none());
    assert!(cache.coverage(&k).await.unwrap().is_empty());
    assert!(cache.put(&lease, 0, data(8)).await.unwrap());
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(8));
}

#[tokio::test]
async fn changed_source_or_length_cannot_reuse_old_bytes() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    let revised = cache
        .lease(Key::new(k.item(), b"different metadata", BLOCK))
        .unwrap();
    assert!(cache.get(&revised, 0).await.is_none());
    let resized = cache.lease(key(BLOCK + 1)).unwrap();
    assert!(cache.get(&resized, 0).await.is_none());
}

#[tokio::test]
async fn active_and_pinned_files_cannot_be_evicted() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    assert!(!cache.evict(&k, "test").await.unwrap());
    drop(lease);
    assert!(cache.pin(&k, true).await.unwrap());
    assert!(!cache.evict(&k, "test").await.unwrap());
    cache.pin(&k, false).await.unwrap();
    assert!(cache.evict(&k, "test").await.unwrap());
    assert!(!t.payload(&k, 0).exists());
}

#[tokio::test]
async fn quota_declines_a_fill_without_evicting_an_active_file() {
    let t = Temp::new();
    let mut settings = t.settings();
    settings.folders[0].quota_bytes = 2 * BLOCK;
    let cache = Cache::open(settings).await.unwrap();
    let k = key(2 * BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    assert!(cache.put(&lease, 0, data(7)).await.unwrap());
    assert!(!cache.put(&lease, 1, data(8)).await.unwrap());
    assert_eq!(cache.stats().await.unwrap().bytes, BLOCK);
}

#[tokio::test]
async fn read_only_folder_serves_old_data_but_cannot_accept_new_fills() {
    let t = Temp::new();
    let mut settings = t.settings();
    let cache = Cache::open(settings.clone()).await.unwrap();
    let k = key(2 * BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    drop(lease);
    drop(cache);
    settings.folders[0].read_only = true;
    let cache = Cache::open(settings).await.unwrap();
    let lease = cache.lease(k).unwrap();
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(7));
    assert!(!cache.put(&lease, 1, data(8)).await.unwrap());
}

#[tokio::test]
async fn another_owner_cannot_open_the_same_folder() {
    let t = Temp::new();
    let settings = t.settings();
    let cache = Cache::open(settings.clone()).await.unwrap();
    assert!(Cache::open(settings).await.is_err());
    assert_eq!(Arc::strong_count(&cache), 1);
}

#[tokio::test]
async fn changing_the_marker_or_mount_declines_reads_and_writes() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(2 * BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    std::fs::rename(t.0.join("data"), t.0.join("previous")).unwrap();
    std::fs::create_dir(t.0.join("data")).unwrap();
    assert!(cache.get(&lease, 0).await.is_none());
    assert!(!cache.put(&lease, 1, data(8)).await.unwrap());
    assert!(!t.payload(&k, 0).exists());
}

#[tokio::test]
async fn a_symlink_payload_never_writes_the_target() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    let victim = t.0.join("victim");
    std::fs::write(&victim, b"keep").unwrap();
    std::os::unix::fs::symlink(&victim, t.payload(&k, 0)).unwrap();
    assert!(!cache.put(&lease, 0, data(7)).await.unwrap());
    assert_eq!(std::fs::read(&victim).unwrap(), b"keep");
    assert!(cache.coverage(&k).await.unwrap().is_empty());
}

#[tokio::test]
async fn saturated_fills_reserve_a_buffer_for_cached_playback() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    let mut held = Vec::new();
    while let Some(slot) = cache.try_buffer() {
        held.push(slot);
    }
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(7));
    assert!(!cache.put(&lease, 0, data(7)).await.unwrap());
    drop(held);
    assert_eq!(cache.get(&lease, 0).await.unwrap(), data(7));
}

#[tokio::test]
async fn different_blocks_of_one_chunk_can_commit_concurrently() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(3 * BLOCK);
    let lease = cache.lease(k).unwrap();
    let (first, second, third) = tokio::join!(
        cache.put(&lease, 0, data(1)),
        cache.put(&lease, 1, data(2)),
        cache.put(&lease, 2, data(3))
    );
    assert!(first.unwrap() && second.unwrap() && third.unwrap());
    for (block, value) in [(0, 1), (1, 2), (2, 3)] {
        assert_eq!(cache.get(&lease, block).await.unwrap(), data(value));
    }
    assert_eq!(cache.stats().await.unwrap().bytes, 3 * BLOCK);
}

#[tokio::test]
async fn pressure_sweep_frees_unpinned_closed_files_and_preserves_readers() {
    let t = Temp::new();
    let mut settings = t.settings();
    settings.folders[0].quota_bytes = 3 * BLOCK;
    let cache = Cache::open(settings).await.unwrap();
    let old_key = key(2 * BLOCK);
    let old = cache.lease(old_key.clone()).unwrap();
    assert!(cache.put(&old, 0, data(1)).await.unwrap());
    assert!(cache.put(&old, 1, data(2)).await.unwrap());
    let new = cache
        .lease(Key::new(Uuid::from_u128(3), b"new", BLOCK))
        .unwrap();
    assert!(!cache.put(&new, 0, data(3)).await.unwrap());
    cache.maintain().await.unwrap();
    assert_eq!(cache.stats().await.unwrap().bytes, 2 * BLOCK);
    drop(old);
    assert!(!cache.put(&new, 0, data(3)).await.unwrap());
    cache.maintain().await.unwrap();
    assert_eq!(cache.stats().await.unwrap().bytes, 0);
    assert!(!t.payload(&old_key, 0).exists());
    assert!(cache.put(&new, 0, data(3)).await.unwrap());
}

#[tokio::test]
async fn client_supplied_size_cannot_bypass_an_active_entry_lease() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    cache.put(&lease, 0, data(7)).await.unwrap();
    let mut entry = cache.entries(0, 1).await.unwrap().remove(0);
    entry.size_bytes = 1;
    let canonical = cache.resolve(&entry).await.unwrap().unwrap();
    assert_eq!(canonical.size(), BLOCK);
    assert!(!cache.evict(&canonical, "test").await.unwrap());
}

#[tokio::test]
async fn opening_an_unowned_nonempty_folder_does_not_modify_it() {
    let t = Temp::new();
    let sentinel = t.0.join("data").join("legacy-payload");
    std::fs::write(&sentinel, b"keep").unwrap();
    assert!(Cache::open(t.settings()).await.is_err());
    let names: Vec<_> = std::fs::read_dir(t.0.join("data"))
        .unwrap()
        .map(|e| e.unwrap().file_name())
        .collect();
    assert_eq!(names, vec![std::ffi::OsString::from("legacy-payload")]);
    assert_eq!(std::fs::read(sentinel).unwrap(), b"keep");
}

#[tokio::test]
async fn complete_copy_verification_rejects_partial_and_corrupt_payloads() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let key = key(BLOCK + 17);
    let lease = cache.lease(key.clone()).unwrap();
    assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(false));
    assert!(cache.put(&lease, 0, data(11)).await.unwrap());
    assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(false));
    assert!(
        cache
            .put(&lease, 1, Bytes::from(vec![12; 17]))
            .await
            .unwrap()
    );
    assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(true));
    std::fs::OpenOptions::new()
        .write(true)
        .open(t.payload(&key, 0))
        .unwrap()
        .write_all_at(b"damage", 0)
        .unwrap();
    assert_eq!(cache.verify_complete(&key).await.unwrap(), Some(false));
    assert!(cache.coverage(&key).await.unwrap()[0].start > 0);
}

#[tokio::test]
async fn probe_pages_confirm_corruption_without_counting_storage_outages_as_damage() {
    let t = Temp::new();
    let cache = Cache::open(t.settings()).await.unwrap();
    let k = key(3 * BLOCK);
    let lease = cache.lease(k.clone()).unwrap();
    for block in 0..3 {
        assert!(cache.put(&lease, block, data(7)).await.unwrap());
    }
    std::fs::OpenOptions::new()
        .write(true)
        .open(t.payload(&k, 0))
        .unwrap()
        .write_all_at(b"corrupt", BLOCK)
        .unwrap();
    let first = cache.probe(&k, 0, 1).await.unwrap();
    assert_eq!((first.verified, first.next_block), (1, Some(1)));
    let bad = cache.probe(&k, first.next_block.unwrap(), 1).await.unwrap();
    assert_eq!((bad.invalidated, bad.next_block), (1, Some(2)));
    let final_page = cache.probe(&k, bad.next_block.unwrap(), 1).await.unwrap();
    assert_eq!((final_page.verified, final_page.next_block), (1, None));
    let before = cache.coverage(&k).await.unwrap();
    assert_eq!(before.len(), 2);
    let diagnostics = cache.diagnostics().await.unwrap();
    assert_eq!(diagnostics[0].invalid_blocks, 1);
    assert_eq!(diagnostics[0].identity_ok, Some(true));
    assert!(diagnostics[0].free_bytes.is_some());
    std::fs::rename(t.0.join("data"), t.0.join("previous")).unwrap();
    let missing_mount = cache.probe(&k, 0, 16).await.unwrap();
    assert_eq!(
        (missing_mount.unavailable, missing_mount.invalidated),
        (1, 0),
        "a missing mount must not invalidate cached coverage"
    );
    std::fs::create_dir(t.0.join("data")).unwrap();
    let diagnostics = cache.diagnostics().await.unwrap();
    assert_eq!(diagnostics[0].identity_ok, Some(false));
    let uncertain = cache.probe(&k, 0, 16).await.unwrap();
    assert_eq!(
        (
            uncertain.unavailable,
            uncertain.invalidated,
            uncertain.next_block
        ),
        (1, 0, Some(0))
    );
    assert_eq!(
        cache
            .coverage(&k)
            .await
            .unwrap()
            .iter()
            .map(|r| (r.start, r.end))
            .collect::<Vec<_>>(),
        before.iter().map(|r| (r.start, r.end)).collect::<Vec<_>>()
    );
    assert!(
        std::fs::read_dir(t.0.join("data"))
            .unwrap()
            .next()
            .is_none(),
        "diagnostics never repair ownership by writing markers"
    );
    std::fs::remove_dir(t.0.join("data")).unwrap();
    std::fs::rename(t.0.join("previous"), t.0.join("data")).unwrap();
    assert_eq!(cache.probe(&k, 0, 16).await.unwrap().verified, 2);
}

#[tokio::test]
async fn eviction_cursors_are_persistent_and_ignore_newer_events_between_pages() {
    let t = Temp::new();
    let settings = t.settings();
    let cache = Cache::open(settings.clone()).await.unwrap();
    for revision in [b"first".as_slice(), b"second"] {
        let k = Key::new(Uuid::from_u128(2), revision, BLOCK);
        let lease = cache.lease(k.clone()).unwrap();
        assert!(cache.put(&lease, 0, data(7)).await.unwrap());
        drop(lease);
        assert!(cache.evict(&k, "manual").await.unwrap());
    }
    let newest = cache.evictions(None, 1).await.unwrap();
    assert_eq!(newest.len(), 1);
    assert_eq!(newest[0].bytes, BLOCK);
    let cursor = newest[0].cursor;
    let older = cache.evictions(Some(cursor), 1).await.unwrap();
    assert_eq!(older.len(), 1);
    assert!(older[0].cursor < cursor);
    assert_ne!(older[0].revision, newest[0].revision);
    drop(cache);
    let cache = Cache::open(settings).await.unwrap();
    assert_eq!(cache.evictions(None, 1).await.unwrap()[0].cursor, cursor);
    assert_eq!(
        cache.evictions(Some(cursor), 1).await.unwrap()[0].cursor,
        older[0].cursor
    );
}
