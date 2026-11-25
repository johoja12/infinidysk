//! Short-lived handoff for adjacent playback ranges that split a cache block.
use crate::{BLOCK, Cache, Key, Lease};
use std::collections::VecDeque;
use std::sync::{MutexGuard, PoisonError};
use std::time::Duration;
use tokio::{sync::OwnedSemaphorePermit, time::Instant};

const PREFIX_TTL: Duration = Duration::from_secs(2);

struct Prefix {
    key: Key,
    end: u64,
    bytes: Vec<u8>,
    // The closure drops the permit when a parked prefix expires or is discarded.
    // Keep the permit private to that prefix so Clippy does not treat Arc<Cache>
    // as a held semaphore permit at every caller.
    take_permit: Box<dyn FnOnce() -> OwnedSemaphorePermit + Send + Sync>,
    expires: Instant,
}

pub(super) struct Prefixes {
    entries: VecDeque<Prefix>,
    limit: usize,
}

impl Prefixes {
    pub(super) fn new(limit: usize) -> Self {
        Self {
            entries: VecDeque::new(),
            limit,
        }
    }

    pub(super) fn discard_oldest(&mut self) -> bool {
        self.entries.pop_front().is_some()
    }

    pub(super) fn expire(&mut self) {
        let now = Instant::now();
        while self.entries.front().is_some_and(|p| p.expires <= now) {
            self.entries.pop_front();
        }
    }
}

impl Cache {
    pub(super) fn playback_prefixes(&self) -> MutexGuard<'_, Prefixes> {
        self.playback_prefixes
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    /// Keep only a complete prefix of one current-revision block. The buffer
    /// continues to count against the existing memory allowance and is reclaimable.
    pub fn remember_playback_prefix(
        &self,
        lease: &Lease,
        end: u64,
        bytes: Vec<u8>,
        permit: OwnedSemaphorePermit,
    ) {
        if end >= lease.key().size() || bytes.is_empty() || bytes.len() as u64 != end % BLOCK {
            return;
        }
        let mut prefixes = self.playback_prefixes();
        prefixes.expire();
        let key = lease.key();
        prefixes.entries.retain(|p| &p.key != key || p.end != end);
        if prefixes.entries.len() >= prefixes.limit {
            prefixes.discard_oldest();
        }
        prefixes.entries.push_back(Prefix {
            key: key.clone(),
            end,
            bytes,
            take_permit: Box::new(move || permit),
            expires: Instant::now() + PREFIX_TTL,
        });
        drop(prefixes);
    }

    /// Resume only at the exact end of clean data from the same source revision.
    pub fn resume_playback_prefix(
        &self,
        lease: &Lease,
        start: u64,
    ) -> Option<(Vec<u8>, OwnedSemaphorePermit)> {
        let mut prefixes = self.playback_prefixes();
        prefixes.expire();
        let index = prefixes
            .entries
            .iter()
            .position(|p| &p.key == lease.key() && p.end == start)?;
        let prefix = prefixes.entries.remove(index)?;
        drop(prefixes);
        Some((prefix.bytes, (prefix.take_permit)()))
    }
}
