//! Bounded provider-facing PAR2 execution. Discovery and durable publication remain
//! separate: only complete, imported range bindings are accepted here, and this
//! executor returns a whole-file proof without modifying source/cache state.
mod articles;
mod discovery;
pub use articles::{ArticleOutcome, ArticleProof, VerifiedArticle};
pub use discovery::{Anchor, DiscoveryBudget, Posted, discover};

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use bytes::Bytes;
use infinidysk_core::{Par2Proof, Segments};
use infinidysk_nntp::{BodyExpectation, Client, Lane, Workload};
use infinidysk_par2::repair::{
    self, Inspection, Limits, Reconstructor, RecoveryReader, RecoverySet, Verification,
    VerifiedRepair,
};
use tokio_util::sync::CancellationToken;

const MAX_ARTICLES: usize = 100_000;
const MAX_RELEASE: u64 = 16 << 30;
const MAX_RECOVERY: usize = 512 << 20;
const MAX_ARTICLE: usize = 32 * 1024 * 1024;
const MAX_PATCH: usize = 256 << 20;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("PAR2 source is not bound to a complete trusted imported range")]
    Binding,
    #[error("PAR2 execution exceeds its release, request, read or memory allowance")]
    Budget,
    #[error(
        "PAR2 damage exceeds the missing-slice allowance ({observed} observed, {maximum} allowed)"
    )]
    MissingLimit { observed: usize, maximum: usize },
    #[error("PAR2 source changed or remained unavailable after reconstruction")]
    Changed,
    #[error("PAR2 source discovery exceeded its time allowance")]
    DiscoveryTimeout,
    #[error("PAR2 execution cancelled")]
    Cancelled,
    #[error("PAR2 CPU worker failed")]
    Worker,
    #[error("PAR2 provider read failed")]
    Provider(#[from] infinidysk_nntp::Error),
    #[error(transparent)]
    Recovery(repair::Error),
}

impl From<repair::Error> for Error {
    fn from(error: repair::Error) -> Self {
        match error {
            repair::Error::Cancelled => Self::Cancelled,
            other => Self::Recovery(other),
        }
    }
}

/// A whole posted file, not an archive member's inferred byte span. The proof is
/// captured with the imported source; file names do not authorize range bindings.
#[derive(Clone)]
pub struct Source {
    pub segments: Segments,
    pub proof: Par2Proof,
}
#[derive(Clone, Copy)]
pub struct Budget {
    pub reconstruction: Limits,
    pub requests: usize,
    /// Returned decoded bytes across inspection, reduction, final verification
    /// and bounded complete-article assembly.
    /// This is an execution allowance, not a provider wire-byte counter.
    pub read_bytes: u64,
}
impl Default for Budget {
    fn default() -> Self {
        Self {
            reconstruction: Limits::default(),
            requests: MAX_ARTICLES,
            read_bytes: 3 * MAX_RELEASE + MAX_RECOVERY as u64 + MAX_PATCH as u64,
        }
    }
}
struct Bound {
    segments: Segments,
    offsets: Vec<u64>,
}
impl Bound {
    fn new(segments: Segments) -> Result<Self, Error> {
        if !segments.sizes_trusted
            || segments.items.is_empty()
            || segments.items.len() > MAX_ARTICLES
        {
            return Err(Error::Binding);
        }
        let mut offsets = Vec::with_capacity(segments.items.len());
        let mut offset = 0u64;
        let mut ids = BTreeSet::new();
        for segment in &segments.items {
            if segment.size == 0
                || segment.size as usize > MAX_ARTICLE
                || segment.id.len() > 4096
                || (!segment.id.is_empty() && !ids.insert(segment.id.as_str()))
            {
                return Err(Error::Binding);
            }
            offsets.push(offset);
            offset = offset
                .checked_add(u64::from(segment.size))
                .filter(|v| *v <= MAX_RELEASE)
                .ok_or(Error::Budget)?;
        }
        Ok(Self { segments, offsets })
    }
}

pub enum Outcome {
    Healthy,
    Repaired(VerifiedRepair),
}

/// Exact source bindings in PAR2 main-packet order. Plan construction performs no
/// provider work and rejects partial/untrusted ranges before any allocation-heavy
/// reconstruction. No health/regrab state is changed by this object.
pub struct Plan {
    set: RecoverySet,
    sources: Vec<Bound>,
    volumes: Vec<Bound>,
    budget: Budget,
}
impl Plan {
    pub fn new(
        set: RecoverySet,
        sources: Vec<Source>,
        volumes: Vec<Segments>,
        mut budget: Budget,
    ) -> Result<Self, Error> {
        if sources.len() != set.files().len()
            || volumes.is_empty()
            || volumes.len() > 128
            || budget.requests == 0
            || budget.requests > MAX_ARTICLES
            || budget.read_bytes == 0
            || !(1..=64).contains(&budget.reconstruction.missing)
        {
            return Err(Error::Binding);
        }
        let mut keyed = BTreeMap::new();
        for source in sources {
            if keyed.insert(source.proof.file_id, source).is_some() {
                return Err(Error::Binding);
            }
        }
        let mut bound = Vec::with_capacity(keyed.len());
        for file in set.files() {
            let source = keyed.remove(&file.id).ok_or(Error::Binding)?;
            let proof = &source.proof;
            if proof.file_length != file.length
                || proof.file_md5 != file.hash
                || proof.md5_16k != file.hash_16k
                || u64::from(proof.slice_size) != set.slice_size() as u64
                || source.segments.size() != file.length
                || proof.slice_md5.len() != file.slices.len()
                || proof.slice_crc32.len() != file.slices.len()
                || proof
                    .slice_md5
                    .iter()
                    .zip(&file.slices)
                    .any(|(v, s)| *v != s.md5)
                || proof
                    .slice_crc32
                    .iter()
                    .zip(&file.slices)
                    .any(|(v, s)| *v != s.crc32)
            {
                return Err(Error::Binding);
            }
            // Empty files have no BODY ranges but still participate in whole-file proof.
            if file.length == 0 && source.segments.items.is_empty() {
                bound.push(Bound {
                    segments: source.segments,
                    offsets: vec![],
                });
            } else {
                bound.push(Bound::new(source.segments)?);
            }
        }
        let volumes = volumes
            .into_iter()
            .map(Bound::new)
            .collect::<Result<Vec<_>, _>>()?;
        let all = bound.iter().chain(&volumes);
        let mut articles = 0usize;
        let mut source_bytes = 0u64;
        let mut recovery_bytes = 0u64;
        let mut heap = set.retained_bytes();
        for (i, source) in all.enumerate() {
            articles = articles
                .checked_add(source.segments.items.len())
                .filter(|n| *n <= MAX_ARTICLES)
                .ok_or(Error::Budget)?;
            if i < bound.len() {
                source_bytes = source_bytes
                    .checked_add(source.segments.size())
                    .filter(|n| *n <= MAX_RELEASE)
                    .ok_or(Error::Budget)?;
            } else {
                recovery_bytes = recovery_bytes
                    .checked_add(source.segments.size())
                    .filter(|n| *n <= MAX_RECOVERY as u64)
                    .ok_or(Error::Budget)?;
            }
            heap = heap
                .checked_add(
                    source.offsets.capacity() * std::mem::size_of::<u64>()
                        + source.segments.items.capacity()
                            * std::mem::size_of::<infinidysk_core::Segment>(),
                )
                .and_then(|n| {
                    n.checked_add(
                        source
                            .segments
                            .items
                            .iter()
                            .map(|s| {
                                s.id.capacity()
                                    + s.fallbacks.capacity() * std::mem::size_of::<String>()
                                    + s.fallbacks.iter().map(String::capacity).sum::<usize>()
                            })
                            .sum::<usize>(),
                    )
                })
                .ok_or(Error::Budget)?;
        }
        // One current decoded article and its replacement may overlap during a read;
        // source/recovery geometry and the recovery packet window count as well.
        let overhead = heap
            .checked_add(2 * MAX_ARTICLE + 8192 + 68)
            .ok_or(Error::Budget)?;
        budget.reconstruction.memory = budget
            .reconstruction
            .memory
            .checked_sub(overhead)
            .ok_or(Error::Budget)?;
        // A missing-slice policy is a ceiling, not an up-front reservation for
        // eight damaged slices. Preflight the minimum workable plan here; the
        // census later checks the actual missing count before recovery reads.
        Reconstructor::new(set.clone(), &[0], budget.reconstruction)?;
        Ok(Self {
            set,
            sources: bound,
            volumes,
            budget,
        })
    }
    pub async fn execute(
        self,
        client: Arc<Client>,
        cancel: CancellationToken,
    ) -> Result<Outcome, Error> {
        self.execute_with_reads(client, cancel)
            .await
            .map(|(outcome, _)| outcome)
    }
    async fn execute_with_reads(
        &self,
        client: Arc<Client>,
        cancel: CancellationToken,
    ) -> Result<(Outcome, Reads), Error> {
        let mut reads = Reads {
            client,
            cancel: cancel.clone(),
            requests: 0,
            bytes: 0,
            budget: self.budget,
            last: None,
            lane: Lane::default(),
        };
        let mut inspection = Inspection::new(self.set.clone(), self.budget.reconstruction.missing)?;
        for (index, at) in self.set.locations().iter().enumerate() {
            let bytes = reads
                .slice(
                    &self.sources[at.file],
                    at.local as u64 * self.set.slice_size() as u64,
                    at.length,
                )
                .await?;
            let token = cancel.clone();
            let maximum = self.budget.reconstruction.missing;
            inspection = tokio::task::spawn_blocking(move || {
                if let Err(error) =
                    inspection.slice(index, bytes.as_deref(), || token.is_cancelled())
                {
                    return Err(if inspection.missing_count() > maximum {
                        Error::MissingLimit {
                            observed: inspection.missing_count(),
                            maximum,
                        }
                    } else {
                        Error::from(error)
                    });
                }
                Ok(inspection)
            })
            .await
            .map_err(|_| Error::Worker)??;
        }
        let token = cancel.clone();
        let missing =
            tokio::task::spawn_blocking(move || inspection.finish(|| token.is_cancelled()))
                .await
                .map_err(|_| Error::Worker)??;
        if missing.is_empty() {
            return Ok((Outcome::Healthy, reads));
        }
        let mut recon = Reconstructor::new(self.set.clone(), &missing, self.budget.reconstruction)?;
        reads.last = None;
        for volume in &self.volumes {
            let mut reader =
                RecoveryReader::new(self.set.id(), self.set.slice_size(), MAX_RECOVERY)?;
            for i in 0..volume.segments.items.len() {
                let Some(bytes) = reads.article(volume, i).await? else {
                    break;
                };
                let token = cancel.clone();
                let (next, next_reader, keep) = tokio::task::spawn_blocking(move || {
                    let keep = reader.push(
                        &bytes,
                        || token.is_cancelled(),
                        |row| {
                            recon.offer(row)?;
                            Ok(!recon.ready())
                        },
                    )?;
                    Ok::<_, Error>((recon, reader, keep))
                })
                .await
                .map_err(|_| Error::Worker)??;
                recon = next;
                reader = next_reader;
                if !keep || recon.ready() {
                    break;
                }
            }
            let token = cancel.clone();
            recon = tokio::task::spawn_blocking(move || {
                reader.finish(
                    || token.is_cancelled(),
                    |row| {
                        recon.offer(row)?;
                        Ok(!recon.ready())
                    },
                )?;
                Ok::<_, Error>(recon)
            })
            .await
            .map_err(|_| Error::Worker)??;
            if recon.ready() {
                break;
            }
        }
        if !recon.ready() {
            return Err(Error::Recovery(repair::Error::Recovery));
        }
        reads.last = None;
        for (index, at) in self.set.locations().iter().enumerate() {
            if missing.binary_search(&index).is_ok() {
                continue;
            }
            let bytes = reads
                .slice(
                    &self.sources[at.file],
                    at.local as u64 * self.set.slice_size() as u64,
                    at.length,
                )
                .await?
                .ok_or(Error::Changed)?;
            let token = cancel.clone();
            recon = tokio::task::spawn_blocking(move || {
                recon.present(index, &bytes, || token.is_cancelled())?;
                Ok::<_, Error>(recon)
            })
            .await
            .map_err(|_| Error::Worker)??;
        }
        let token = cancel.clone();
        let recovered = tokio::task::spawn_blocking(move || recon.finish(|| token.is_cancelled()))
            .await
            .map_err(|_| Error::Worker)??;
        let mut verification = Verification::new(self.set.clone(), recovered)?;
        reads.last = None;
        for (index, at) in self.set.locations().iter().enumerate() {
            let bytes = if missing.binary_search(&index).is_ok() {
                None
            } else {
                Some(
                    reads
                        .slice(
                            &self.sources[at.file],
                            at.local as u64 * self.set.slice_size() as u64,
                            at.length,
                        )
                        .await?
                        .ok_or(Error::Changed)?,
                )
            };
            let token = cancel.clone();
            verification = tokio::task::spawn_blocking(move || {
                verification.slice(index, bytes.as_deref(), || token.is_cancelled())?;
                Ok::<_, Error>(verification)
            })
            .await
            .map_err(|_| Error::Worker)??;
        }
        let proof =
            tokio::task::spawn_blocking(move || verification.finish(|| cancel.is_cancelled()))
                .await
                .map_err(|_| Error::Worker)??;
        Ok((Outcome::Repaired(proof), reads))
    }
}
struct Reads {
    client: Arc<Client>,
    lane: Lane,
    cancel: CancellationToken,
    requests: usize,
    bytes: u64,
    budget: Budget,
    last: Option<Remembered>,
}
struct Remembered {
    id: String,
    offset: u64,
    file_size: u64,
    size: u32,
    bytes: Option<Bytes>,
}
impl Reads {
    async fn article(&mut self, source: &Bound, index: usize) -> Result<Option<Bytes>, Error> {
        if self.cancel.is_cancelled() {
            return Err(Error::Cancelled);
        }
        let segment = &source.segments.items[index];
        if segment.id.is_empty() {
            return Ok(None);
        }
        if let Some(last) = &self.last
            && last.id == segment.id
            && last.offset == source.offsets[index]
            && last.file_size == source.segments.size()
            && last.size == segment.size
        {
            return Ok(last.bytes.clone());
        }
        self.requests = self
            .requests
            .checked_add(1)
            .filter(|n| *n <= self.budget.requests)
            .ok_or(Error::Budget)?;
        // Reserve the maximum successful response before admission, including a
        // cancelled/drained BODY. No allowance is refunded after dispatch.
        self.bytes = self
            .bytes
            .checked_add(u64::from(segment.size))
            .filter(|n| *n <= self.budget.read_bytes)
            .ok_or(Error::Budget)?;
        let body = tokio::select! {
            () = self.cancel.cancelled() => return Err(Error::Cancelled),
            result = self.client.body_checked_in_lane(&segment.id, self.lane, Workload::Health, BodyExpectation {
                offset: Some(source.offsets[index]), decoded_len: Some(u64::from(segment.size)),
                file_size: Some(source.segments.size()),
                ..BodyExpectation::default()
            }) => result,
        };
        let bytes = match body {
            Ok(body) => Some(body.data),
            Err(
                infinidysk_nntp::Error::NotFound
                | infinidysk_nntp::Error::CrcMismatch { .. }
                | infinidysk_nntp::Error::Yenc(_),
            ) => None,
            Err(error) => return Err(Error::Provider(error)),
        };
        self.last = Some(Remembered {
            id: segment.id.clone(),
            offset: source.offsets[index],
            file_size: source.segments.size(),
            size: segment.size,
            bytes: bytes.clone(),
        });
        Ok(bytes)
    }
    async fn slice(
        &mut self,
        source: &Bound,
        offset: u64,
        length: usize,
    ) -> Result<Option<Vec<u8>>, Error> {
        let mut index = source
            .offsets
            .partition_point(|v| *v <= offset)
            .saturating_sub(1);
        let mut out = Vec::with_capacity(length);
        while out.len() < length {
            let bytes = self.article(source, index).await?;
            let Some(bytes) = bytes else {
                return Ok(None);
            };
            let within = usize::try_from(offset + out.len() as u64 - source.offsets[index])
                .map_err(|_| Error::Binding)?;
            let take = (length - out.len()).min(bytes.len() - within);
            out.extend_from_slice(&bytes[within..within + take]);
            index += 1;
        }
        Ok(Some(out))
    }
}
