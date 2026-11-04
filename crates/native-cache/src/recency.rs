//! Best-effort eviction timestamps never gate verified playback bytes.
use sqlx::SqlitePool;
use std::collections::HashMap;
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};
use tokio::sync::mpsc;

pub(super) struct Updates {
    sender: mpsc::Sender<(String, i64)>,
    dropped: Arc<AtomicU64>,
}
impl Updates {
    pub(super) fn start(db: SqlitePool) -> Self {
        let (sender, mut receiver) = mpsc::channel::<(String, i64)>(1024);
        let dropped = Arc::new(AtomicU64::new(0));
        let failed = Arc::clone(&dropped);
        // The worker owns no Cache/folder/lease. Closing the sender drains at most
        // the bounded queue and releases its database clone, including on restart.
        tokio::spawn(async move {
            while let Some((key, at)) = receiver.recv().await {
                let mut batch = HashMap::from([(key, at)]);
                for _ in 0..127 {
                    let Ok((key, at)) = receiver.try_recv() else {
                        break;
                    };
                    batch
                        .entry(key)
                        .and_modify(|old| *old = (*old).max(at))
                        .or_insert(at);
                }
                let result=async {
                    let mut tx=db.begin().await?;
                    for (key,at) in &batch {
                        sqlx::query("UPDATE entries SET used=CASE WHEN used<$2 THEN $2 ELSE used END WHERE key=$1")
                            .bind(key).bind(at).execute(&mut *tx).await?;
                    }
                    tx.commit().await
                }.await;
                if result.is_err() {
                    failed.fetch_add(batch.len() as u64, Ordering::Relaxed);
                    tracing::debug!(
                        reason = "catalogue recency update unavailable",
                        "native cache recency deferred"
                    );
                }
            }
        });
        Self { sender, dropped }
    }
    pub(super) fn touch(&self, key: String, at: i64) {
        if self.sender.try_send((key, at)).is_err() {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }
    pub(super) fn pending(&self) -> usize {
        self.sender.max_capacity() - self.sender.capacity()
    }
    pub(super) fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }
}
