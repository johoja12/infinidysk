//! A bounded, process-local mount challenge. Never persisted or listed as media.
use infinidysk_core::ItemId;
use std::sync::{Mutex, PoisonError};
use std::time::{Duration, Instant};
use uuid::Uuid;

const TTL: Duration = Duration::from_secs(60);
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Probe {
    id: ItemId,
    bytes: [u8; 16],
    until: Instant,
    expires_at_ms: i64,
}
impl Probe {
    pub const fn id(&self) -> ItemId {
        self.id
    }
    pub const fn expires_at_ms(&self) -> i64 {
        self.expires_at_ms
    }
    pub fn data(&self) -> &[u8] {
        &self.bytes
    }
}
#[derive(Default)]
pub(super) struct Registry(Mutex<Option<Probe>>);
impl Registry {
    pub(super) fn issue(&self) -> Probe {
        self.issue_at(Instant::now(), infinidysk_db::now_ms())
    }
    fn issue_at(&self, now: Instant, wall: i64) -> Probe {
        let mut slot = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(probe) = slot.as_ref().filter(|p| now < p.until) {
            return probe.clone();
        }
        let probe = Probe {
            id: ItemId::new(),
            bytes: *Uuid::new_v4().as_bytes(),
            until: now + TTL,
            expires_at_ms: wall.saturating_add(60_000),
        };
        *slot = Some(probe.clone());
        probe
    }
    pub(super) fn lookup(&self, id: ItemId) -> Option<Probe> {
        self.lookup_at(id, Instant::now())
    }
    fn lookup_at(&self, id: ItemId, now: Instant) -> Option<Probe> {
        let mut slot = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        if slot.as_ref().is_some_and(|p| now >= p.until) {
            *slot = None;
        }
        slot.as_ref().filter(|p| p.id == id).cloned()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn bounded_challenges_are_instance_local_and_expire_monotonically() {
        let a = Registry::default();
        let b = Registry::default();
        let now = Instant::now();
        let first = a.issue_at(now, 1000);
        assert_eq!(a.issue_at(now + Duration::from_secs(5), 0), first);
        assert!(b.lookup_at(first.id, now).is_none());
        assert!(a.lookup_at(first.id, now + TTL).is_none());
        let second = a.issue_at(now + TTL, 0);
        assert_ne!(second.id, first.id);
        assert_ne!(second.bytes, first.bytes);
        assert!(a.lookup_at(first.id, now + TTL).is_none());
    }
}
