//! Original-NZB source discovery. Names are only probe hints; imported proofs and
//! exact article IDs bind damaged sources. Healthy additional sources need full MD5.
use super::{
    Budget, Error, MAX_ARTICLE, MAX_ARTICLES, MAX_PATCH, MAX_RECOVERY, MAX_RELEASE, Plan, Source,
};
use infinidysk_core::{Par2Proof, Segment, Segments};
use infinidysk_nntp::{Body, BodyExpectation, Client, Lane, Workload};
use infinidysk_par2::{
    File,
    repair::{MetadataReader, RecoverySet},
};
use md5::{Digest as _, Md5};
use std::{collections::BTreeSet, sync::Arc, time::Duration};
use tokio_util::sync::CancellationToken;

/// One original posted file. A hint can prioritize scanning but never bind a source.
pub struct Posted {
    pub ids: Vec<String>,
    pub recovery_hint: bool,
}
/// Proof retained by the imported item, including an exact contiguous article-ID
/// span from a whole source or archive volume. No file name supplies this authority.
pub struct Anchor {
    pub proof: Par2Proof,
    pub ids: Vec<String>,
}
#[derive(Clone, Copy)]
pub struct DiscoveryBudget {
    pub execution: Budget,
    pub requests: usize,
    /// Returned decoded bytes, not provider wire bytes. An unknown BODY reserves
    /// `MAX_ARTICLE` until its complete response proves the actual decoded length.
    pub read_bytes: u64,
}
impl Default for DiscoveryBudget {
    fn default() -> Self {
        Self {
            execution: Budget::default(),
            requests: MAX_ARTICLES,
            read_bytes: 4 * MAX_RELEASE + 2 * MAX_RECOVERY as u64 + MAX_PATCH as u64,
        }
    }
}
#[derive(Clone, Copy)]
struct Span {
    offset: u64,
    length: u32,
}
struct Captured {
    post: Posted,
    spans: Vec<Option<Span>>,
    length: Option<u64>,
    whole: Option<[u8; 16]>,
    first: Option<[u8; 16]>,
    recovery: bool,
}
struct Probe {
    spans: Vec<Option<Span>>,
    length: Option<u64>,
    whole: Md5,
    complete: bool,
    prefix: Vec<u8>,
    reader: Option<MetadataReader>,
    hint: bool,
    scanned: usize,
}
impl Probe {
    fn push(&mut self, index: usize, body: &Body, cancel: &CancellationToken) -> Result<(), Error> {
        if cancel.is_cancelled() {
            return Err(Error::Cancelled);
        }
        if body.data.is_empty()
            || body.data.len() > MAX_ARTICLE
            || body.file_size > MAX_RELEASE
            || body
                .offset
                .checked_add(body.data.len() as u64)
                .is_none_or(|end| end > body.file_size)
            || self.length.is_some_and(|length| length != body.file_size)
        {
            return Err(Error::Binding);
        }
        self.length = Some(body.file_size);
        self.spans[index] = Some(Span {
            offset: body.offset,
            length: u32::try_from(body.data.len()).map_err(|_| Error::Budget)?,
        });
        if self.complete {
            self.whole.update(&body.data);
        }
        if body.offset == self.prefix.len() as u64 && self.prefix.len() < 16 << 10 {
            let take = ((16 << 10) - self.prefix.len()).min(body.data.len());
            self.prefix.extend_from_slice(&body.data[..take]);
        }
        if self.reader.is_none() && (self.hint || body.data.starts_with(b"PAR2\0PKT")) {
            self.reader = Some(MetadataReader::new(None, MAX_RECOVERY)?);
        }
        if let Some(reader) = &mut self.reader {
            self.scanned = self
                .scanned
                .checked_add(body.data.len())
                .filter(|n| *n <= MAX_RECOVERY)
                .ok_or(Error::Budget)?;
            reader.push(&body.data, || cancel.is_cancelled())?;
        }
        if cancel.is_cancelled() {
            return Err(Error::Cancelled);
        }
        Ok(())
    }
}
impl Captured {
    fn matches(&self, ids: &[String]) -> bool {
        contains(&self.post.ids, ids)
    }
    fn geometry(&self, known_length: Option<u64>) -> Result<Option<Segments>, Error> {
        let Some(length) = self.length.or(known_length) else {
            return Ok(None);
        };
        let mut spans = self.spans.clone();
        for i in 0..spans.len() {
            if spans[i].is_some() {
                continue;
            }
            let start = if i == 0 {
                0
            } else if let Some(previous) = spans[i - 1] {
                previous.offset + u64::from(previous.length)
            } else {
                return Ok(None);
            };
            let end = if i + 1 == spans.len() {
                length
            } else if let Some(next) = spans[i + 1] {
                next.offset
            } else {
                return Ok(None);
            };
            let Some(size) = end
                .checked_sub(start)
                .filter(|n| *n > 0 && *n <= MAX_ARTICLE as u64)
            else {
                return Ok(None);
            };
            spans[i] = Some(Span {
                offset: start,
                length: u32::try_from(size).map_err(|_| Error::Budget)?,
            });
        }
        let mut offset = 0;
        let mut items = Vec::with_capacity(spans.len());
        let mut missing = Vec::new();
        for (i, (span, id)) in spans.into_iter().zip(&self.post.ids).enumerate() {
            let Some(span) = span else {
                return Ok(None);
            };
            if span.offset != offset {
                return Ok(None);
            }
            offset += u64::from(span.length);
            if self.spans[i].is_none() {
                missing.push(u32::try_from(i).map_err(|_| Error::Budget)?);
            }
            items.push(Segment {
                id: id.clone(),
                size: span.length,
                fallbacks: vec![],
            });
        }
        if offset != length {
            return Ok(None);
        }
        Ok(Some(Segments {
            items,
            sizes_trusted: true,
            missing,
            corrupt: vec![],
        }))
    }
}
fn contains(post: &[String], anchor: &[String]) -> bool {
    let Some(first) = anchor.first() else {
        return false;
    };
    post.iter()
        .position(|id| id == first)
        .is_some_and(|at| post.get(at..at + anchor.len()) == Some(anchor))
}
fn proof(set: &RecoverySet, file: &File) -> Result<Par2Proof, Error> {
    Ok(Par2Proof {
        file_length: file.length,
        file_md5: file.hash,
        md5_16k: file.hash_16k,
        file_id: file.id,
        slice_size: u32::try_from(set.slice_size()).map_err(|_| Error::Budget)?,
        slice_md5: file.slices.iter().map(|s| s.md5).collect(),
        slice_crc32: file.slices.iter().map(|s| s.crc32).collect(),
    })
}
fn accepts(set: &RecoverySet, anchors: &[Anchor]) -> bool {
    anchors.iter().all(|anchor| {
        let p = &anchor.proof;
        set.files()
            .iter()
            .find(|file| file.id == p.file_id)
            .is_some_and(|file| {
                p.file_length == file.length
                    && p.file_md5 == file.hash
                    && p.md5_16k == file.hash_16k
                    && u64::from(p.slice_size) == set.slice_size() as u64
                    && p.slice_md5.len() == file.slices.len()
                    && p.slice_crc32.len() == file.slices.len()
                    && p.slice_md5
                        .iter()
                        .zip(&file.slices)
                        .all(|(hash, slice)| *hash == slice.md5)
                    && p.slice_crc32
                        .iter()
                        .zip(&file.slices)
                        .all(|(crc, slice)| *crc == slice.crc32)
            })
    })
}
fn compatible(first: &RecoverySet, next: &RecoverySet) -> bool {
    first.slice_size() == next.slice_size()
        && first.files().len() == next.files().len()
        && first.files().iter().zip(next.files()).all(|(a, b)| {
            a.id == b.id
                && a.length == b.length
                && a.hash == b.hash
                && a.hash_16k == b.hash_16k
                && a.slices == b.slices
        })
}
fn preflight(posts: &[Posted], anchors: &[Anchor], budget: DiscoveryBudget) -> Result<(), Error> {
    if posts.is_empty()
        || posts.len() > 512
        || anchors.is_empty()
        || anchors.len() > 64
        || budget.requests == 0
        || budget.requests > MAX_ARTICLES
        || budget.read_bytes < MAX_ARTICLE as u64
    {
        return Err(Error::Budget);
    }
    let mut ids = BTreeSet::new();
    let mut heap = 3 * MAX_ARTICLE + (16 << 20);
    for post in posts {
        if post.ids.is_empty() {
            return Err(Error::Binding);
        }
        for id in &post.ids {
            if id.is_empty() || id.len() > 4096 || !ids.insert(id.as_str()) {
                return Err(Error::Binding);
            }
            // Input, captured spans, final source/volume copies and lookup nodes.
            heap = heap
                .checked_add(3 * (id.capacity() + std::mem::size_of::<String>()) + 128)
                .ok_or(Error::Budget)?;
            if ids.len() > MAX_ARTICLES || heap > budget.execution.reconstruction.memory {
                return Err(Error::Budget);
            }
        }
    }
    for anchor in anchors {
        if anchor.ids.is_empty() || !posts.iter().any(|p| contains(&p.ids, &anchor.ids)) {
            return Err(Error::Binding);
        }
        heap = heap
            .checked_add(
                anchor.proof.slice_md5.capacity() * 16
                    + anchor.proof.slice_crc32.capacity() * 4
                    + anchor
                        .ids
                        .iter()
                        .map(|id| id.capacity() + std::mem::size_of::<String>())
                        .sum::<usize>(),
            )
            .ok_or(Error::Budget)?;
    }
    if heap > budget.execution.reconstruction.memory {
        return Err(Error::Budget);
    }
    Ok(())
}
/// Discover original-NZB ranges and bind a repair plan without mutating health,
/// storage or Arr. Unknown ranges, unavailable providers and limits are not proof
/// that a release is irreparable. A whole attempt is bounded at two minutes.
pub async fn discover(
    client: Arc<Client>,
    posts: Vec<Posted>,
    anchors: Vec<Anchor>,
    budget: DiscoveryBudget,
    cancel: CancellationToken,
) -> Result<Plan, Error> {
    let token = cancel.child_token();
    let _cancel = token.clone().drop_guard();
    tokio::time::timeout(Duration::from_secs(120), async move {
        let token_check = token.clone();
        let (posts, anchors) = tokio::task::spawn_blocking(move || {
            if token_check.is_cancelled() {
                return Err(Error::Cancelled);
            }
            preflight(&posts, &anchors, budget)?;
            Ok::<_, Error>((posts, anchors))
        })
        .await
        .map_err(|_| Error::Worker)??;
        discover_inner(client, posts, anchors, budget, token).await
    })
    .await
    .map_err(|_| Error::DiscoveryTimeout)?
}
async fn discover_inner(
    client: Arc<Client>,
    mut posts: Vec<Posted>,
    anchors: Vec<Anchor>,
    mut budget: DiscoveryBudget,
    cancel: CancellationToken,
) -> Result<Plan, Error> {
    posts.sort_by_key(|post| !post.recovery_hint);
    let anchors: Arc<[Anchor]> = anchors.into();
    let lane = Lane::default();
    let mut captures = Vec::with_capacity(posts.len());
    let mut chosen: Option<RecoverySet> = None;
    let mut recovery = 0u64;
    let mut total = 0u64;
    for post in posts {
        let mut probe = Probe {
            spans: vec![None; post.ids.len()],
            length: None,
            whole: Md5::new(),
            complete: true,
            prefix: Vec::new(),
            reader: None,
            hint: post.recovery_hint,
            scanned: 0,
        };
        for (i, id) in post.ids.iter().enumerate() {
            if cancel.is_cancelled() {
                return Err(Error::Cancelled);
            }
            budget.requests = budget.requests.checked_sub(1).ok_or(Error::Budget)?;
            // The maximum response remains reserved if the caller leaves or its
            // outcome is uncertain. Only a complete response releases unused space.
            budget.read_bytes = budget
                .read_bytes
                .checked_sub(MAX_ARTICLE as u64)
                .ok_or(Error::Budget)?;
            let result = tokio::select! { ()=cancel.cancelled()=>return Err(Error::Cancelled),
            result=client.body_checked_in_lane(id,lane,Workload::Health,BodyExpectation::default())=>result };
            match result {
                Ok(body) => {
                    budget.read_bytes += MAX_ARTICLE as u64 - body.data.len() as u64;
                    let token = cancel.clone();
                    probe = tokio::task::spawn_blocking(move || {
                        probe.push(i, &body, &token)?;
                        Ok::<_, Error>(probe)
                    })
                    .await
                    .map_err(|_| Error::Worker)??;
                }
                Err(infinidysk_nntp::Error::NotFound) => {
                    budget.read_bytes += MAX_ARTICLE as u64;
                    probe.complete = false;
                }
                Err(
                    infinidysk_nntp::Error::CrcMismatch { .. } | infinidysk_nntp::Error::Yenc(_),
                ) => {
                    // All configured copies were conclusively unusable. Keep the
                    // full reservation for their discarded decoded data.
                    probe.complete = false;
                }
                Err(error) => return Err(Error::Provider(error)),
            }
        }
        let token = cancel.clone();
        let anchors_check = anchors.clone();
        let (capture, next_chosen) = tokio::task::spawn_blocking(move || {
            if token.is_cancelled() {
                return Err(Error::Cancelled);
            }
            let recovery = probe.reader.is_some();
            let set = if let Some(reader) = probe.reader {
                match reader.finish(|| token.is_cancelled()) {
                    Ok(set) => Some(set),
                    Err(infinidysk_par2::repair::Error::Invalid(_)) => None,
                    Err(error) => return Err(Error::from(error)),
                }
            } else {
                None
            };
            if let Some(set) = set
                && accepts(&set, &anchors_check)
            {
                if let Some(old) = &chosen {
                    if old.id() == set.id() && !compatible(old, &set) {
                        return Err(Error::Binding);
                    }
                } else {
                    chosen = Some(set);
                }
            }
            if token.is_cancelled() {
                return Err(Error::Cancelled);
            }
            let first = probe
                .length
                .filter(|n| probe.prefix.len() as u64 == (*n).min(16 << 10))
                .map(|_| Md5::digest(&probe.prefix).into());
            let whole = probe.complete.then(|| probe.whole.finalize().into());
            Ok((
                Captured {
                    post,
                    spans: probe.spans,
                    length: probe.length,
                    whole,
                    first,
                    recovery,
                },
                chosen,
            ))
        })
        .await
        .map_err(|_| Error::Worker)??;
        total = total
            .checked_add(capture.length.unwrap_or(0))
            .filter(|n| *n <= MAX_RELEASE + MAX_RECOVERY as u64)
            .ok_or(Error::Budget)?;
        if capture.recovery {
            recovery = recovery
                .checked_add(capture.length.unwrap_or(0))
                .filter(|n| *n <= MAX_RECOVERY as u64)
                .ok_or(Error::Budget)?;
        }
        chosen = next_chosen;
        captures.push(capture);
    }
    let set = chosen.ok_or(Error::Binding)?;
    let token = cancel.clone();
    tokio::task::spawn_blocking(move || bind(set, &captures, &anchors, budget, &token))
        .await
        .map_err(|_| Error::Worker)?
}
fn bind(
    set: RecoverySet,
    captures: &[Captured],
    anchors: &[Anchor],
    mut budget: DiscoveryBudget,
    cancel: &CancellationToken,
) -> Result<Plan, Error> {
    if cancel.is_cancelled() {
        return Err(Error::Cancelled);
    }
    let mut sources = Vec::with_capacity(set.files().len());
    let mut used = BTreeSet::new();
    for file in set.files() {
        let proof = proof(&set, file)?;
        if file.length == 0 {
            sources.push(Source {
                segments: Segments::default(),
                proof,
            });
            continue;
        }
        let anchored: Vec<_> = anchors
            .iter()
            .filter(|anchor| anchor.proof.file_id == file.id)
            .collect();
        let matching: Vec<_> = captures
            .iter()
            .enumerate()
            .filter(|(index, capture)| {
                !used.contains(index)
                    && if anchored.is_empty() {
                        capture.whole == Some(file.hash)
                            && capture.first == Some(file.hash_16k)
                            && capture.length == Some(file.length)
                    } else {
                        anchored.iter().all(|anchor| capture.matches(&anchor.ids))
                            && capture.length.is_none_or(|n| n == file.length)
                    }
            })
            .collect();
        if matching.len() != 1 {
            return Err(Error::Binding);
        }
        let (index, capture) = matching[0];
        used.insert(index);
        let segments = capture.geometry(Some(file.length))?.ok_or(Error::Binding)?;
        sources.push(Source { segments, proof });
    }
    let volumes = captures
        .iter()
        .filter(|capture| capture.recovery)
        .filter_map(|capture| capture.geometry(None).transpose())
        .collect::<Result<Vec<_>, _>>()?;
    budget.execution.requests = budget.execution.requests.min(budget.requests);
    budget.execution.read_bytes = budget.execution.read_bytes.min(budget.read_bytes);
    if cancel.is_cancelled() {
        return Err(Error::Cancelled);
    }
    if budget.execution.requests == 0 || budget.execution.read_bytes == 0 {
        return Err(Error::Budget);
    }
    Plan::new(set, sources, volumes, budget.execution)
}
