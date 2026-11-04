//! Physical storage work remains visible after the async caller times out.
use serde::Serialize;
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};
use std::time::Instant;
use utoipa::ToSchema;
#[derive(Clone, Debug, Serialize, ToSchema)]
#[schema(as=NativeCacheIo)]
pub struct Stats {
    pub read_requests: u64,
    pub write_requests: u64,
    pub removal_requests: u64,
    pub verified_read_bytes: u64,
    /// Physical writes completed and synced; catalogue publication is counted separately.
    pub synced_write_bytes: u64,
    pub queued: u64,
    pub active: u64,
    pub failed: u64,
    pub elapsed_ms: u64,
    pub available_buffer_blocks: usize,
}
#[derive(Clone, Copy)]
pub(super) enum Kind {
    Read,
    Write,
    Remove,
}
#[derive(Default)]
pub(super) struct Counters {
    reads: AtomicU64,
    writes: AtomicU64,
    removals: AtomicU64,
    read_bytes: AtomicU64,
    write_bytes: AtomicU64,
    queued: AtomicU64,
    active: AtomicU64,
    failed: AtomicU64,
    elapsed: AtomicU64,
}
pub(super) struct Work {
    counters: Arc<Counters>,
    kind: Kind,
    started: Option<Instant>,
    success: bool,
}
impl Counters {
    pub(super) fn begin(self: &Arc<Self>, kind: Kind) -> Work {
        match kind {
            Kind::Read => &self.reads,
            Kind::Write => &self.writes,
            Kind::Remove => &self.removals,
        }
        .fetch_add(1, Ordering::Relaxed);
        self.queued.fetch_add(1, Ordering::Relaxed);
        Work {
            counters: self.clone(),
            kind,
            started: None,
            success: false,
        }
    }
    pub(super) fn snapshot(&self, available_buffer_blocks: usize) -> Stats {
        let get = |n: &AtomicU64| n.load(Ordering::Relaxed);
        Stats {
            read_requests: get(&self.reads),
            write_requests: get(&self.writes),
            removal_requests: get(&self.removals),
            verified_read_bytes: get(&self.read_bytes),
            synced_write_bytes: get(&self.write_bytes),
            queued: get(&self.queued),
            active: get(&self.active),
            failed: get(&self.failed),
            elapsed_ms: get(&self.elapsed),
            available_buffer_blocks,
        }
    }
}
impl Work {
    pub(super) fn start(&mut self) {
        if self.started.is_none() {
            self.counters.queued.fetch_sub(1, Ordering::Relaxed);
            self.counters.active.fetch_add(1, Ordering::Relaxed);
            self.started = Some(Instant::now());
        }
    }
    pub(super) fn success(&mut self, bytes: u64) {
        if self.success {
            return;
        }
        self.success = true;
        match self.kind {
            Kind::Read => {
                self.counters.read_bytes.fetch_add(bytes, Ordering::Relaxed);
            }
            Kind::Write => {
                self.counters
                    .write_bytes
                    .fetch_add(bytes, Ordering::Relaxed);
            }
            Kind::Remove => {}
        }
    }
}
impl Drop for Work {
    fn drop(&mut self) {
        if let Some(started) = self.started {
            self.counters.active.fetch_sub(1, Ordering::Relaxed);
            self.counters.elapsed.fetch_add(
                u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX),
                Ordering::Relaxed,
            );
        } else {
            self.counters.queued.fetch_sub(1, Ordering::Relaxed);
        }
        if !self.success {
            self.counters.failed.fetch_add(1, Ordering::Relaxed);
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn caller_timeout_does_not_hide_still_running_storage_work() {
        let counters = Arc::new(Counters::default());
        let mut work = counters.begin(Kind::Write);
        assert_eq!(counters.snapshot(2).queued, 1);
        let (started, ready) = tokio::sync::oneshot::channel();
        let (finish, wait) = std::sync::mpsc::channel();
        let (completed, done) = tokio::sync::oneshot::channel();
        let io = tokio::task::spawn_blocking(move || {
            work.start();
            let _ = started.send(());
            wait.recv().unwrap();
            work.success(4096);
            drop(work);
            let _ = completed.send(());
        });
        ready.await.unwrap();
        assert!(
            tokio::time::timeout(std::time::Duration::from_millis(1), io)
                .await
                .is_err()
        );
        assert_eq!(counters.snapshot(1).active, 1);
        finish.send(()).unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), done)
            .await
            .unwrap()
            .unwrap();
        let stats = counters.snapshot(2);
        assert_eq!(stats.active, 0);
        assert_eq!(stats.queued, 0);
        assert_eq!(stats.synced_write_bytes, 4096);
        assert_eq!(stats.failed, 0);
    }
    #[test]
    fn abandoned_queued_and_failed_reads_are_not_successful_bytes() {
        let counters = Arc::new(Counters::default());
        drop(counters.begin(Kind::Write));
        let mut read = counters.begin(Kind::Read);
        read.start();
        drop(read);
        let stats = counters.snapshot(3);
        assert_eq!(stats.queued, 0);
        assert_eq!(stats.active, 0);
        assert_eq!(stats.failed, 2);
        assert_eq!(stats.verified_read_bytes, 0);
    }
}
