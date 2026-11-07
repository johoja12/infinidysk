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
    pending: Arc<AtomicU64>,
}
impl Updates {
    pub(super) fn start(db: SqlitePool) -> Self {
        Self::spawn(db, false)
    }
    pub(super) fn start_legacy(db: SqlitePool) -> Self {
        Self::spawn(db, true)
    }
    fn spawn(db: SqlitePool, legacy: bool) -> Self {
        let (sender, mut receiver) = mpsc::channel::<(String, i64)>(1024);
        let dropped = Arc::new(AtomicU64::new(0));
        let failed = Arc::clone(&dropped);
        let pending = Arc::new(AtomicU64::new(0));
        let outstanding = pending.clone();
        // The worker owns no Cache/folder/lease. Closing the sender drains at most
        // the bounded queue and releases its database clone, including on restart.
        tokio::spawn(async move {
            while let Some((key, at)) = receiver.recv().await {
                let mut batch = HashMap::from([(key, at)]);
                let mut count = 1;
                for _ in 0..127 {
                    let Ok((key, at)) = receiver.try_recv() else {
                        break;
                    };
                    count += 1;
                    batch
                        .entry(key)
                        .and_modify(|old| *old = (*old).max(at))
                        .or_insert(at);
                }
                let result=async {
                    let mut tx=db.begin().await?;
                    for (key,at) in &batch {
                        let sql = if legacy {
                            "UPDATE Entries SET Access=MAX(Access,$2) WHERE Key=$1 AND Access<$2"
                        } else {
                            "UPDATE entries SET used=CASE WHEN used<$2 THEN $2 ELSE used END WHERE key=$1"
                        };
                        sqlx::query(sql)
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
                outstanding.fetch_sub(count, Ordering::Release);
            }
        });
        Self {
            sender,
            dropped,
            pending,
        }
    }
    pub(super) fn touch(&self, key: String, at: i64) {
        self.pending.fetch_add(1, Ordering::Relaxed);
        if self.sender.try_send((key, at)).is_err() {
            self.pending.fetch_sub(1, Ordering::Release);
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }
    pub(super) fn pending(&self) -> usize {
        usize::try_from(self.pending.load(Ordering::Acquire)).unwrap_or(usize::MAX)
    }
    pub(super) fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }
}
