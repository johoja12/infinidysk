//! Complete article assembly after whole-file verification. Unchanged portions of
//! an affected article are re-read and IFSC-checked before they can be published.
use super::{Error, MAX_PATCH, Outcome, Plan, Reads};
use bytes::Bytes;
use infinidysk_nntp::Body;
use infinidysk_nntp::Client;
use infinidysk_par2::Id;
use infinidysk_par2::repair::{self, VerifiedRepair};
use std::{collections::BTreeSet, sync::Arc};
use tokio_util::sync::CancellationToken;

/// A complete article whose bytes are covered by a whole-file PAR2 proof. Private
/// fields prevent callers from mistaking an arbitrary byte buffer for this proof.
pub struct VerifiedArticle {
    id: String,
    body: Body,
    proof: ArticleProof,
}
/// Source identity carried from the independently bound PAR2 recovery set.
pub struct ArticleProof {
    set: Id,
    file: Id,
    md5: Id,
}
impl ArticleProof {
    pub const fn set(&self) -> Id {
        self.set
    }
    pub const fn file(&self) -> Id {
        self.file
    }
    pub const fn md5(&self) -> Id {
        self.md5
    }
}
impl VerifiedArticle {
    pub fn into_parts(self) -> (String, Body, ArticleProof) {
        (self.id, self.body, self.proof)
    }
}
/// Complete verified articles, or a source that required no reconstruction.
pub enum ArticleOutcome {
    Healthy,
    Repaired(Vec<VerifiedArticle>),
}
impl Plan {
    /// Reconstruct, prove every recoverable file, then assemble complete affected
    /// articles under the same request/read allowance. This does not publish them.
    pub async fn execute_articles(
        self,
        client: Arc<Client>,
        cancel: CancellationToken,
    ) -> Result<ArticleOutcome, Error> {
        let (outcome, mut reads) = self.execute_with_reads(client, cancel).await?;
        let Outcome::Repaired(proof) = outcome else {
            return Ok(ArticleOutcome::Healthy);
        };
        self.assemble(proof, &mut reads)
            .await
            .map(ArticleOutcome::Repaired)
    }
    async fn assemble(
        &self,
        proof: VerifiedRepair,
        reads: &mut Reads,
    ) -> Result<Vec<VerifiedArticle>, Error> {
        if proof.set() != self.set.id() {
            return Err(Error::Binding);
        }
        let mut affected = BTreeSet::new();
        let mut bytes = proof.slices().iter().try_fold(0usize, |n, s| {
            n.checked_add(s.data.capacity() + std::mem::size_of::<repair::Recovered>())
                .ok_or(Error::Budget)
        })?;
        let mut payload = 0usize;
        let mut ids = BTreeSet::new();
        for recovered in proof.slices() {
            let at = self.set.locations()[recovered.index];
            let begin = at.local as u64 * self.set.slice_size() as u64;
            let end = begin + at.length as u64;
            let source = &self.sources[at.file];
            let first = source
                .offsets
                .partition_point(|v| *v <= begin)
                .saturating_sub(1);
            for index in first..source.segments.items.len() {
                let offset = source.offsets[index];
                if offset >= end {
                    break;
                }
                if !affected.insert((at.file, index)) {
                    continue;
                }
                let segment = &source.segments.items[index];
                if segment.id.is_empty() || !ids.insert(segment.id.as_str()) {
                    return Err(Error::Binding);
                }
                bytes = bytes
                    .checked_add(segment.size as usize + segment.id.len() + 512)
                    .ok_or(Error::Budget)?;
                payload = payload
                    .checked_add(segment.size as usize)
                    .ok_or(Error::Budget)?;
                if affected.len() > 1024
                    || payload > MAX_PATCH
                    || bytes > self.budget.reconstruction.memory
                {
                    return Err(Error::Budget);
                }
            }
        }
        // All payloads are preflighted before allocating any article. Recovered
        // slices remain resident; current decoded article + slice buffers were
        // reserved by Plan::new's fixed overhead.
        let mut articles = Vec::with_capacity(affected.len());
        reads.last = None;
        for (file, index) in affected {
            if reads.cancel.is_cancelled() {
                return Err(Error::Cancelled);
            }
            let source = &self.sources[file];
            let segment = &source.segments.items[index];
            let start = source.offsets[index];
            let end = start + u64::from(segment.size);
            let mut assembled = Vec::with_capacity(segment.size as usize);
            let first_slice =
                usize::try_from(start / self.set.slice_size() as u64).map_err(|_| Error::Budget)?;
            let last_slice = usize::try_from((end - 1) / self.set.slice_size() as u64)
                .map_err(|_| Error::Budget)?;
            for local in first_slice..=last_slice {
                if reads.cancel.is_cancelled() {
                    return Err(Error::Cancelled);
                }
                let offset = local as u64 * self.set.slice_size() as u64;
                let length = usize::try_from(
                    (self.set.files()[file].length - offset).min(self.set.slice_size() as u64),
                )
                .map_err(|_| Error::Budget)?;
                let from =
                    usize::try_from(start.saturating_sub(offset)).map_err(|_| Error::Binding)?;
                let to = usize::try_from((end - offset).min(length as u64))
                    .map_err(|_| Error::Binding)?;
                let token = reads.cancel.clone();
                let copy = |data: &[u8], out: &mut Vec<u8>| -> Result<(), Error> {
                    for chunk in data[from..to].chunks(8192) {
                        if token.is_cancelled() {
                            return Err(Error::Cancelled);
                        }
                        out.extend_from_slice(chunk);
                    }
                    Ok(())
                };
                if let Some(recovered) = proof.slices().iter().find(|s| {
                    let at = self.set.locations()[s.index];
                    at.file == file && at.local == local
                }) {
                    copy(&recovered.data, &mut assembled)?;
                } else {
                    let data = reads
                        .slice(source, offset, length)
                        .await?
                        .ok_or(Error::Changed)?;
                    let checksum = self.set.files()[file].slices[local];
                    let size = self.set.slice_size() as u64;
                    let token = reads.cancel.clone();
                    let data = tokio::task::spawn_blocking(move || {
                        if token.is_cancelled() {
                            return Err(Error::Cancelled);
                        }
                        if !checksum.matches(size, &data) {
                            return Err(Error::Changed);
                        }
                        if token.is_cancelled() {
                            return Err(Error::Cancelled);
                        }
                        Ok(data)
                    })
                    .await
                    .map_err(|_| Error::Worker)??;
                    copy(&data, &mut assembled)?;
                }
            }
            if assembled.len() != segment.size as usize {
                return Err(Error::Binding);
            }
            articles.push(VerifiedArticle {
                id: segment.id.clone(),
                body: Body {
                    data: Bytes::from(assembled),
                    offset: start,
                    file_size: source.segments.size(),
                    part: None,
                    total: None,
                },
                proof: ArticleProof {
                    set: proof.set(),
                    file: self.set.files()[file].id,
                    md5: self.set.files()[file].hash,
                },
            });
        }
        if reads.cancel.is_cancelled() {
            return Err(Error::Cancelled);
        }
        Ok(articles)
    }
}
