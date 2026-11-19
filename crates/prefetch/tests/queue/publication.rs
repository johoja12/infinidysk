//! Controlled storage latency around a real disk cache; source reads use loopback NNTP.
use super::*;
use bytes::Bytes;
use infinidysk_native_cache::{Error, legacy};
use std::sync::atomic::{AtomicUsize, Ordering};
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

struct SlowStorage {
    cache: Arc<Cache>,
    started: Arc<Notify>,
    release: Arc<Semaphore>,
    writes: Arc<AtomicUsize>,
}
impl legacy::Reader for SlowStorage {
    fn writable(&self) -> bool {
        true
    }
    fn read<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
        block: u64,
        _permit: OwnedSemaphorePermit,
    ) -> legacy::ReadFuture<'a> {
        Box::pin(async move {
            let lease = self.cache.lease(key.clone()).unwrap();
            Ok(self.cache.get(&lease, block).await)
        })
    }
    fn coverage<'a>(&'a self, key: &'a infinidysk_native_cache::Key) -> legacy::CoverageFuture<'a> {
        Box::pin(self.cache.coverage(key))
    }
    fn write<'a>(
        &'a self,
        key: &'a infinidysk_native_cache::Key,
        block: u64,
        data: Bytes,
        permit: OwnedSemaphorePermit,
    ) -> legacy::WriteFuture<'a> {
        let (cache, key, started, release, writes) = (
            self.cache.clone(),
            key.clone(),
            self.started.clone(),
            self.release.clone(),
            self.writes.clone(),
        );
        Box::pin(async move {
            // Match the owned compatible writer: cancellation cannot release its
            // buffer permit or interrupt durable publication after admission.
            tokio::spawn(async move {
                writes.fetch_add(1, Ordering::Relaxed);
                started.notify_one();
                release.acquire().await.unwrap().forget();
                let lease = cache.lease(key).unwrap();
                cache
                    .put_reserved_background(&lease, block, data, permit)
                    .await
            })
            .await
            .map_err(|_| Error::Invalid("controlled publication task failed"))?
        })
    }
}
async fn storage(t: &Temp, cache: &Cache) -> Arc<SlowStorage> {
    let root = t.0.join("slow-storage");
    std::fs::create_dir_all(&root).unwrap();
    let storage = Arc::new(SlowStorage {
        cache: Cache::open(Settings {
            enabled: true,
            min_file_mb: 0,
            metadata_path: t.0.join("slow-metadata").to_str().unwrap().into(),
            folders: vec![FolderSettings {
                id: Uuid::from_u128(3),
                path: root.to_str().unwrap().into(),
                min_free_bytes: 0,
                ..FolderSettings::default()
            }],
            ..Settings::default()
        })
        .await
        .unwrap(),
        started: Arc::new(Notify::new()),
        release: Arc::new(Semaphore::new(0)),
        writes: Arc::new(AtomicUsize::new(0)),
    });
    cache.set_legacy_reader(storage.clone()).unwrap();
    storage
}
async fn first_block(q: &Queue, item: &Item) -> infinidysk_prefetch::Job {
    q.enqueue(
        item.id,
        &[Range {
            start: 0,
            end: BLOCK,
        }],
        50,
        Uuid::new_v4(),
    )
    .await
    .unwrap()
}
#[tokio::test]
async fn warming_awaits_slow_publication_without_false_deferral_or_duplicate_source_reads() {
    let t = Temp::new();
    let (vfs, item, cache, corpus, server, _client) = fixture(&t, false).await;
    let storage = storage(&t, &cache).await;
    let q = Arc::new(Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap());
    let job = first_block(&q, &item).await;
    let mut worker = tokio::spawn({
        let q = q.clone();
        async move { q.tick(&settings()).await }
    });
    tokio::time::timeout(Duration::from_secs(10), storage.started.notified())
        .await
        .unwrap();
    assert!(
        tokio::time::timeout(Duration::from_millis(1200), &mut worker)
            .await
            .is_err()
    );
    let status = q.status().await;
    assert_eq!(status.jobs[0].verified_bytes, 0);
    assert_eq!(status.jobs[0].next_attempt, job.next_attempt);
    assert_eq!(status.jobs[0].reason, None);
    storage.release.add_permits(1);
    assert!(worker.await.unwrap().unwrap());
    let status = q.status().await;
    assert_eq!(status.jobs[0].state, State::Complete);
    assert_eq!(status.jobs[0].verified_bytes, BLOCK);
    assert_eq!(status.jobs[0].source_bytes, BLOCK);
    assert_eq!(status.jobs[0].reason, None);
    let before = server.body_commands();
    assert!(!q.tick(&settings()).await.unwrap());
    let lease = cache.lease(vfs.cache_key(&item).await.unwrap()).unwrap();
    assert_eq!(
        cache.get(&lease, 0).await.unwrap().as_ref(),
        &corpus.data[..BLOCK_BYTES]
    );
    assert_eq!(server.body_commands(), before);
    assert_eq!(storage.writes.load(Ordering::Relaxed), 1);
}
#[tokio::test]
async fn slow_publication_allows_controls_and_fences_progress_after_pause_or_cancel() {
    for operation in [None, Some(Operation::Pause), Some(Operation::Cancel)] {
        let t = Temp::new();
        let (vfs, item, cache, _corpus, server, _client) = fixture(&t, false).await;
        let storage = storage(&t, &cache).await;
        let q = Arc::new(Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap());
        let job = first_block(&q, &item).await;
        let worker = tokio::spawn({
            let q = q.clone();
            async move { q.tick(&settings()).await }
        });
        tokio::time::timeout(Duration::from_secs(10), storage.started.notified())
            .await
            .unwrap();
        tokio::time::timeout(Duration::from_secs(1), async {
            match operation {
                None => q.pause(true).await,
                Some(operation) => q.control(job.id, operation).await,
            }
        })
        .await
        .unwrap()
        .unwrap();
        assert!(!worker.is_finished());
        assert!(
            q.status().await.rates.iter().any(|rate| rate.active),
            "pause/cancel must not hide an admitted write from drain checks"
        );
        storage.release.add_permits(1);
        assert!(worker.await.unwrap().unwrap());
        assert_eq!(q.status().await.jobs[0].verified_bytes, 0);
        assert!(!q.status().await.rates.iter().any(|rate| rate.active));
        let before = server.body_commands();
        match operation {
            None => q.pause(false).await.unwrap(),
            Some(Operation::Pause) => q.control(job.id, Operation::Resume).await.unwrap(),
            Some(Operation::Cancel) => {
                assert_eq!(q.status().await.jobs[0].state, State::Cancelled);
                assert!(!q.tick(&settings()).await.unwrap());
                continue;
            }
            _ => unreachable!(),
        }
        assert!(q.tick(&settings()).await.unwrap());
        assert_eq!(q.status().await.jobs[0].state, State::Complete);
        assert_eq!(
            server.body_commands(),
            before,
            "resume reuses the durable write"
        );
        assert_eq!(storage.writes.load(Ordering::Relaxed), 1);
    }
}
#[tokio::test]
async fn cancelled_window_keeps_publication_admission_and_drain_visibility_until_commit() {
    let t = Temp::new();
    let (vfs, item, cache, _corpus, server, _client) = fixture(&t, false).await;
    let storage = storage(&t, &cache).await;
    let q = Queue::load(vfs.db().clone(), vfs.clone()).await.unwrap();
    first_block(&q, &item).await;
    let worker = tokio::spawn({
        let q = q.clone();
        async move { q.tick(&settings()).await }
    });
    tokio::time::timeout(Duration::from_secs(10), storage.started.notified())
        .await
        .unwrap();
    worker.abort();
    assert!(worker.await.unwrap_err().is_cancelled());
    assert!(q.status().await.rates.iter().any(|r| r.active));
    let before = server.body_commands();
    assert!(
        !q.tick(&settings()).await.unwrap(),
        "the owned write retains admission"
    );
    storage.release.add_permits(1);
    tokio::time::timeout(Duration::from_secs(5), async {
        while q.status().await.rates.iter().any(|r| r.active) {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert_eq!(q.status().await.jobs[0].verified_bytes, 0);
    assert!(q.tick(&settings()).await.unwrap());
    assert_eq!(q.status().await.jobs[0].state, State::Complete);
    assert_eq!(server.body_commands(), before);
    assert_eq!(storage.writes.load(Ordering::Relaxed), 1);
}
#[tokio::test]
async fn playback_keeps_its_deadline_and_owned_publication_keeps_its_buffer() {
    let t = Temp::new();
    let (vfs, item, cache, corpus, _server, _client) = fixture(&t, false).await;
    let storage = storage(&t, &cache).await;
    let key = vfs.cache_key(&item).await.unwrap();
    let lease = cache.lease(key.clone()).unwrap();
    let buffers = cache.stats().await.unwrap().io.available_buffer_blocks;
    let mut worker = tokio::spawn({
        let cache = cache.clone();
        let lease = lease.clone();
        let data = Bytes::copy_from_slice(&corpus.data[..BLOCK_BYTES]);
        async move { cache.put(&lease, 0, data).await }
    });
    storage.started.notified().await;
    assert!(
        !tokio::time::timeout(Duration::from_secs(2), &mut worker)
            .await
            .unwrap()
            .unwrap()
            .unwrap()
    );
    assert_eq!(
        cache.stats().await.unwrap().io.available_buffer_blocks,
        buffers - 1
    );
    storage.release.add_permits(1);
    tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            if cache.get(&lease, 0).await.is_some() {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert_eq!(storage.writes.load(Ordering::Relaxed), 1);
}
