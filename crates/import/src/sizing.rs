//! Explicit migration-only admission. Failed reads retain their full allowance.
use infinidysk_nntp::{Body, Client, Error, Workload};
use serde::{Deserialize, Serialize};
use std::{
    future::Future,
    pin::Pin,
    sync::{Arc, Mutex},
};
use tokio::sync::Semaphore;

const ARTICLE: u64 = 32 * 1024 * 1024; // NNTP's maximum decoded body.

#[derive(Default, Clone, Copy, Serialize, Deserialize)]
pub struct Usage {
    pub attempted: u64,
    pub succeeded: u64,
    pub decoded_bytes: u64,
    pub charged_bytes: u64,
}
pub type Checkpoint =
    Arc<dyn Fn(Usage) -> Pin<Box<dyn Future<Output = Result<(), ()>> + Send>> + Send + Sync>;
pub struct Sizing {
    checkpoint: Option<Checkpoint>,
    max_articles: u64,
    max_bytes: u64,
    state: Mutex<Usage>,
    gate: Semaphore,
}
impl Sizing {
    #[must_use]
    pub fn new(max_articles: u64, max_decoded_bytes: u64) -> Self {
        Self {
            checkpoint: None,
            max_articles,
            max_bytes: max_decoded_bytes,
            state: Mutex::default(),
            gate: Semaphore::new(1),
        }
    }
    pub fn checkpointed(
        max_articles: u64,
        max_decoded_bytes: u64,
        initial: Usage,
        checkpoint: Checkpoint,
    ) -> Result<Self, Error> {
        if initial.succeeded > initial.attempted || initial.decoded_bytes > initial.charged_bytes {
            return Err(Error::InvalidArgument(
                "migration sizing checkpoint is invalid",
            ));
        }
        Ok(Self {
            checkpoint: Some(checkpoint),
            max_articles,
            max_bytes: max_decoded_bytes,
            state: Mutex::new(initial),
            gate: Semaphore::new(1),
        })
    }
    async fn persist(&self, usage: Usage) -> Result<(), Error> {
        if let Some(save) = &self.checkpoint {
            save(usage)
                .await
                .map_err(|()| Error::InvalidArgument("migration sizing checkpoint unavailable"))?;
        }
        Ok(())
    }
    pub fn usage(&self) -> Result<Usage, Error> {
        self.state
            .lock()
            .map(|state| *state)
            .map_err(|_| Error::InvalidArgument("migration sizing allowance unavailable"))
    }
    fn reserve(&self) -> Result<(), Error> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| Error::InvalidArgument("migration sizing allowance unavailable"))?;
        if state.attempted >= self.max_articles
            || self.max_bytes.saturating_sub(state.charged_bytes) < ARTICLE
        {
            return Err(Error::InvalidArgument(
                "migration sizing allowance exhausted",
            ));
        }
        state.attempted += 1;
        state.charged_bytes += ARTICLE;
        drop(state);
        Ok(())
    }
    pub(crate) async fn body(&self, client: &Client, id: &str) -> Result<Body, Error> {
        let _permit = self.gate.acquire().await.map_err(|_| Error::Unavailable)?;
        self.reserve()?;
        self.persist(self.usage()?).await?;
        // Never cancel a physical read to admit another one. NNTP owns transport deadlines.
        let result = client.body(id, Workload::Queue, None).await;
        if let Ok(body) = &result {
            let bytes = body.data.len() as u64;
            if bytes > ARTICLE {
                return Err(Error::TooLarge);
            }
            let mut next = self.usage()?;
            next.charged_bytes -= ARTICLE - bytes;
            next.decoded_bytes += bytes;
            next.succeeded += 1;
            self.persist(next).await?;
            *self
                .state
                .lock()
                .map_err(|_| Error::InvalidArgument("migration sizing allowance unavailable"))? =
                next;
        }
        result
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn allowances_reserve_worst_case_and_fail_closed() {
        let disabled = Sizing::new(0, ARTICLE);
        assert!(disabled.reserve().is_err());
        let short = Sizing::new(2, ARTICLE - 1);
        assert!(short.reserve().is_err());
        let bounded = Sizing::new(2, ARTICLE);
        bounded.reserve().unwrap();
        assert!(bounded.reserve().is_err());
        assert_eq!(bounded.usage().unwrap().charged_bytes, ARTICLE);
        let count = Sizing::new(1, 2 * ARTICLE);
        count.reserve().unwrap();
        assert!(count.reserve().is_err());
    }
}
