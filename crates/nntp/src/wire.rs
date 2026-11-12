//! Durable admission of warming response bytes. Credits are granted by the owner
//! only after persistence. Unused credits are lost conservatively across restart.
use std::{
    future::Future,
    io,
    pin::Pin,
    sync::{Arc, Mutex, PoisonError},
    time::{SystemTime, UNIX_EPOCH},
};
use tokio::sync::Mutex as AsyncMutex;

pub type Grant = Arc<
    dyn Fn(i64, u64, u64) -> Pin<Box<dyn Future<Output = Result<u64, Denied>> + Send>>
        + Send
        + Sync,
>;
#[derive(Debug, thiserror::Error)]
#[error("warming wire allowance unavailable or exhausted")]
pub struct Denied;
impl From<Denied> for io::Error {
    fn from(value: Denied) -> Self {
        Self::other(value)
    }
}
#[derive(Default)]
struct State {
    day: i64,
    epoch: u64,
    available: u64,
    received: u64,
    limit: u64,
    enabled: bool,
    paused: bool,
    denied: bool,
}
struct Refill {
    epoch: u64,
    task: tokio::task::JoinHandle<Result<(), Denied>>,
}
pub struct Budget {
    state: Mutex<State>,
    refill: AsyncMutex<Option<Refill>>,
    grant: Grant,
}
impl Budget {
    pub fn new(grant: Grant) -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::default(),
            refill: AsyncMutex::new(None),
            grant,
        })
    }
    pub fn configure(&self, enabled: bool, limit: u64) {
        let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if s.enabled != enabled || s.limit != limit {
            s.available = 0;
            s.denied = false;
            s.epoch = s.epoch.wrapping_add(1);
            s.enabled = enabled;
            s.limit = limit;
        }
    }
    pub fn pause(&self, paused: bool) {
        let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if s.paused != paused {
            s.available = 0;
            s.denied = false;
            s.epoch = s.epoch.wrapping_add(1);
            s.paused = paused;
        }
    }
    pub fn blocked(&self) -> bool {
        let s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        s.denied || !s.enabled || s.paused
    }
    pub fn received(&self) -> u64 {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .received
    }
    fn today() -> i64 {
        SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| {
            i64::try_from(d.as_secs() / 86400).unwrap_or(i64::MAX)
        })
    }
    fn advance(s: &mut State, day: i64) {
        if day > s.day {
            s.day = day;
            s.available = 0;
            s.denied = false;
            s.epoch = s.epoch.wrapping_add(1);
        }
    }
    pub(crate) fn take(self: &Arc<Self>, maximum: usize) -> Option<Credit> {
        self.take_at(maximum, Self::today())
    }
    fn take_at(self: &Arc<Self>, maximum: usize, day: i64) -> Option<Credit> {
        let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        Self::advance(&mut s, day);
        if !s.enabled || s.paused || s.available == 0 {
            return None;
        }
        let amount = s.available.min(maximum as u64);
        s.available -= amount;
        Some(Credit {
            budget: self.clone(),
            epoch: s.epoch,
            amount,
            used: 0,
        })
    }
    pub async fn ready(self: Arc<Self>) -> Result<(), Denied> {
        self.ready_at(Self::today()).await
    }
    async fn ready_at(self: &Arc<Self>, day: i64) -> Result<(), Denied> {
        loop {
            let mut refill = self.refill.lock().await;
            let (day, epoch, limit) = {
                let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
                Self::advance(&mut s, day);
                if !s.enabled || s.paused || s.limit == 0 {
                    return Err(Denied);
                }
                if s.available > 0 {
                    s.denied = false;
                    return Ok(());
                }
                (s.day, s.epoch, s.limit)
            };
            if refill.is_none() {
                // A transport may leave after the durable reservation commits.
                // Keep its accounting task in the budget so cancellation cannot
                // lose the granted credit. There is at most one refill task.
                let owner = Arc::downgrade(self);
                let grant = self.grant.clone();
                *refill = Some(Refill {
                    epoch,
                    task: tokio::spawn(async move {
                        let granted = grant(day, 1_000_000, limit).await;
                        let Some(owner) = owner.upgrade() else {
                            return Err(Denied);
                        };
                        let mut s = owner.state.lock().unwrap_or_else(PoisonError::into_inner);
                        if s.epoch != epoch || !s.enabled || s.paused {
                            // Policy changes still retire the old reservation. They
                            // must not deny or credit a newer policy's reads.
                            return Ok(());
                        }
                        let result = match granted {
                            Ok(amount) if amount > 0 && amount <= 1_000_000 => {
                                s.available = s.available.saturating_add(amount);
                                s.denied = false;
                                Ok(())
                            }
                            _ => {
                                s.denied = true;
                                Err(Denied)
                            }
                        };
                        drop(s);
                        result
                    }),
                });
            }
            let Some(task) = refill.as_mut() else {
                return Err(Denied);
            };
            let pending_epoch = task.epoch;
            let result = (&mut task.task).await;
            *refill = None;
            drop(refill);
            if self
                .state
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .epoch
                != pending_epoch
            {
                continue;
            }
            result.map_err(|_| Denied)??;
        }
    }
}
impl Drop for Budget {
    fn drop(&mut self) {
        if let Some(task) = self.refill.get_mut().take() {
            task.task.abort();
        }
    }
}
pub(crate) struct Credit {
    budget: Arc<Budget>,
    epoch: u64,
    amount: u64,
    used: u64,
}
impl Credit {
    pub(crate) fn len(&self) -> usize {
        usize::try_from(self.amount).unwrap_or(usize::MAX)
    }
    pub(crate) fn used(&mut self, bytes: usize) {
        self.used = (bytes as u64).min(self.amount);
    }
}
impl Drop for Credit {
    fn drop(&mut self) {
        let mut s = self
            .budget
            .state
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        s.received = s.received.saturating_add(self.used);
        if s.epoch == self.epoch {
            s.available = s.available.saturating_add(self.amount - self.used);
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    #[tokio::test]
    async fn credits_precede_reads_refund_pending_and_never_survive_policy_or_day_changes() {
        let charged = Arc::new(AtomicU64::new(0));
        let b = Budget::new(Arc::new({
            let charged = charged.clone();
            move |_, _, limit| {
                let charged = charged.clone();
                Box::pin(async move {
                    charged.fetch_add(limit, Ordering::SeqCst);
                    Ok(limit)
                })
            }
        }));
        b.configure(true, 100);
        assert!(b.take_at(20, 10).is_none());
        b.ready_at(10).await.unwrap();
        assert_eq!(charged.load(Ordering::SeqCst), 100);
        let mut c = b.take_at(80, 10).unwrap();
        c.used(30);
        drop(c);
        assert_eq!(b.received(), 30);
        let pending = b.take_at(70, 10).unwrap();
        drop(pending);
        assert_eq!(b.take_at(100, 10).unwrap().len(), 70);
        let old = b.take_at(60, 10).unwrap();
        b.configure(true, 50);
        drop(old);
        assert!(b.take_at(1, 10).is_none());
        b.ready_at(10).await.unwrap();
        let old = b.take_at(20, 10).unwrap();
        b.pause(true);
        drop(old);
        assert!(b.take_at(1, 10).is_none());
        assert!(b.ready_at(10).await.is_err());
        b.pause(false);
        b.ready_at(10).await.unwrap();
        let old = b.take_at(20, 10).unwrap();
        assert!(b.take_at(1, 11).is_none());
        drop(old);
        assert!(b.take_at(1, 11).is_none());
        b.ready_at(11).await.unwrap();
        assert_eq!(
            b.take_at(100, 9).unwrap().len(),
            50,
            "clock rollback cannot create a fresh day"
        );
    }
    #[tokio::test]
    async fn cancelled_waiter_does_not_lose_a_durable_wire_grant() {
        let charged = Arc::new(AtomicU64::new(0));
        let started = Arc::new(tokio::sync::Notify::new());
        let release = Arc::new(tokio::sync::Semaphore::new(0));
        let b = Budget::new(Arc::new({
            let (charged, started, release) = (charged.clone(), started.clone(), release.clone());
            move |_, requested, _| {
                let (charged, started, release) =
                    (charged.clone(), started.clone(), release.clone());
                Box::pin(async move {
                    charged.fetch_add(requested, Ordering::SeqCst);
                    started.notify_one();
                    release.acquire().await.unwrap().forget();
                    Ok(requested)
                })
            }
        }));
        b.configure(true, 2_000_000);
        let waiting = tokio::spawn(b.clone().ready());
        started.notified().await;
        waiting.abort();
        assert!(waiting.await.unwrap_err().is_cancelled());
        release.add_permits(4);
        b.clone().ready().await.unwrap();
        assert_eq!(
            charged.load(Ordering::SeqCst),
            1_000_000,
            "the persisted grant must reach the shared budget even when its original waiter leaves"
        );
        let mut credit = b.take(1_000_000).unwrap();
        assert_eq!(credit.len(), 1_000_000);
        credit.used(1_000_000);
        drop(credit);
        assert_eq!(b.received(), 1_000_000);
    }
    #[tokio::test]
    async fn a_cancelled_refill_cannot_credit_or_deny_a_new_policy() {
        for fail_old in [false, true] {
            let calls = Arc::new(AtomicU64::new(0));
            let started = Arc::new(tokio::sync::Notify::new());
            let release = Arc::new(tokio::sync::Semaphore::new(0));
            let b = Budget::new(Arc::new({
                let (calls, started, release) = (calls.clone(), started.clone(), release.clone());
                move |_, _, limit| {
                    let first = calls.fetch_add(1, Ordering::SeqCst) == 0;
                    let (started, release) = (started.clone(), release.clone());
                    Box::pin(async move {
                        started.notify_one();
                        release.acquire().await.unwrap().forget();
                        if first && fail_old {
                            Err(Denied)
                        } else {
                            Ok(limit)
                        }
                    })
                }
            }));
            b.configure(true, 100);
            let waiting = tokio::spawn(b.clone().ready());
            started.notified().await;
            waiting.abort();
            assert!(waiting.await.unwrap_err().is_cancelled());
            b.pause(true);
            b.configure(true, 50);
            b.pause(false);
            release.add_permits(4);
            b.clone().ready().await.unwrap();
            assert_eq!(b.take(100).unwrap().len(), 50);
            assert!(!b.blocked());
            assert_eq!(calls.load(Ordering::SeqCst), 2);
        }
    }

    #[tokio::test]
    async fn dropping_the_budget_aborts_its_unfinished_refill() {
        use std::sync::atomic::AtomicBool;
        struct Dropped(Arc<AtomicBool>);
        impl Drop for Dropped {
            fn drop(&mut self) {
                self.0.store(true, Ordering::SeqCst);
            }
        }
        let dropped = Arc::new(AtomicBool::new(false));
        let started = Arc::new(tokio::sync::Notify::new());
        let b = Budget::new(Arc::new({
            let (dropped, started) = (dropped.clone(), started.clone());
            move |_, _, _| {
                let (dropped, started) = (dropped.clone(), started.clone());
                Box::pin(async move {
                    let _guard = Dropped(dropped);
                    started.notify_one();
                    std::future::pending().await
                })
            }
        }));
        b.configure(true, 100);
        let waiting = tokio::spawn(b.clone().ready());
        started.notified().await;
        waiting.abort();
        assert!(waiting.await.unwrap_err().is_cancelled());
        drop(b);
        tokio::time::timeout(std::time::Duration::from_secs(1), async {
            while !dropped.load(Ordering::SeqCst) {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
    }
}
