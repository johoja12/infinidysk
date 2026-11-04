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
pub struct Budget {
    state: Mutex<State>,
    refill: AsyncMutex<()>,
    grant: Grant,
}
impl Budget {
    pub fn new(grant: Grant) -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::default(),
            refill: AsyncMutex::new(()),
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
    async fn ready_at(&self, day: i64) -> Result<(), Denied> {
        let _refill = self.refill.lock().await;
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
        let granted = match (self.grant)(day, 1_000_000, limit).await {
            Ok(value) => value,
            Err(error) => {
                self.state
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .denied = true;
                return Err(error);
            }
        };
        if granted == 0 || granted > 1_000_000 {
            self.state
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .denied = true;
            return Err(Denied);
        }
        let mut s = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if s.epoch != epoch || !s.enabled || s.paused {
            s.denied = true;
            return Err(Denied);
        }
        s.available = s.available.saturating_add(granted);
        s.denied = false;
        drop(s);
        Ok(())
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
}
