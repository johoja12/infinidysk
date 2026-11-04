//! Read-only source snapshots for durable PAR2 attempts. Display names never bind
//! input data. Availability annotations do not change the source identity.
use anyhow::{Context as _, ensure};
use infinidysk_core::{BlobId, FileMeta, ItemId, blob};
use infinidysk_db::{Db, Item};
use infinidysk_health::par2::{Anchor, Posted};
use sha2::{Digest as _, Sha256};
use std::collections::{BTreeMap, HashSet};
const MAX_METADATA: u64 = 16 << 20;

pub(super) struct Scope {
    pub item: Item,
    pub meta: FileMeta,
    pub raw: Vec<u8>,
    pub digest: [u8; 32],
    pub faults: [u8; 32],
    pub damaged: bool,
}
pub(super) struct Input {
    pub nzb: BlobId,
    pub nzb_hash: [u8; 32],
    pub posts: Vec<Posted>,
    pub anchors: Vec<Anchor>,
}
pub(super) async fn scope(db: &Db, id: ItemId) -> anyhow::Result<Option<Scope>> {
    let Some(item) = db.item(id).await? else {
        return Ok(None);
    };
    let Some(id) = item.meta_blob else {
        return Ok(None);
    };
    let Some(raw) = db.blob_limited(id, MAX_METADATA).await? else {
        return Ok(None);
    };
    tokio::task::spawn_blocking(move || {
        let original = blob::decode_meta_limited(
            &raw,
            usize::try_from(MAX_METADATA).context("metadata allowance")?,
        )?;
        let mut meta = original.clone();
        let mut fault_hash = Sha256::new();
        fault_hash.update(b"par2-faults-v1");
        let mut damaged = false;
        let mut clear =
            |part: usize, segments: &mut infinidysk_core::Segments| -> anyhow::Result<()> {
                for (kind, indices) in [(0u8, &segments.missing), (1u8, &segments.corrupt)] {
                    let mut indices = indices.clone();
                    indices.sort_unstable();
                    indices.dedup();
                    for index in indices {
                        ensure!(
                            (index as usize) < segments.items.len(),
                            "stored repair damage indices are invalid"
                        );
                        damaged = true;
                        fault_hash.update(part.to_le_bytes());
                        fault_hash.update([kind]);
                        fault_hash.update(index.to_le_bytes());
                    }
                }
                segments.missing.clear();
                segments.corrupt.clear();
                Ok(())
            };
        match &mut meta {
            FileMeta::Nzb(file) => clear(0, &mut file.segments)?,
            FileMeta::Multipart(file) => {
                for (i, part) in file.parts.iter_mut().enumerate() {
                    clear(i, &mut part.segments)?;
                }
            }
        }
        let mut hash = Sha256::new();
        hash.update(b"par2-source-v1");
        hash.update(id.0.as_bytes());
        hash.update(blob::encode_meta(&meta)?);
        Ok(Some(Scope {
            item,
            meta: original,
            raw,
            digest: hash.finalize().into(),
            faults: fault_hash.finalize().into(),
            damaged,
        }))
    })
    .await
    .context("repair metadata worker failed")?
}
async fn original_nzb(db: &Db, item: &Item) -> anyhow::Result<Option<BlobId>> {
    let mut current = item.clone();
    let mut seen = HashSet::new();
    for _ in 0..256 {
        ensure!(seen.insert(current.id), "repair ancestry contains a cycle");
        if let Some(nzb) = current.nzb_blob {
            return Ok(Some(nzb));
        }
        let Some(parent) = current.parent else {
            return Ok(None);
        };
        let Some(item) = db.item(parent).await? else {
            return Ok(None);
        };
        current = item;
    }
    Ok(None)
}
pub(super) async fn input(db: &Db, scope: &Scope) -> anyhow::Result<Option<Input>> {
    let Some(id) = original_nzb(db, &scope.item).await? else {
        return Ok(None);
    };
    let Some(raw) = db.blob_limited(id, MAX_METADATA).await? else {
        return Ok(None);
    };
    let meta = scope.meta.clone();
    tokio::task::spawn_blocking(move || {
        let hash = Sha256::digest(&raw).into();
        let xml = blob::decode_nzb_limited(
            &raw,
            usize::try_from(MAX_METADATA).context("NZB metadata allowance")?,
        )?;
        let nzb = infinidysk_queue::nzb::parse(&xml).context("original repair NZB is invalid")?;
        let posts = nzb
            .files
            .into_iter()
            // Part-number gaps cannot be filled by guessing a surviving ID's range.
            .filter(|file| !file.gap)
            .map(|file| {
                let recovery_hint = file.name().to_ascii_lowercase().ends_with(".par2");
                Posted {
                    ids: file
                        .segments
                        .into_iter()
                        .map(|segment| segment.id)
                        .collect(),
                    recovery_hint,
                }
            })
            .collect();
        let mut anchored = BTreeMap::new();
        let mut add = |segments: &infinidysk_core::Segments,
                       proof: &Option<infinidysk_core::Par2Proof>|
         -> anyhow::Result<()> {
            if let Some(proof) = proof {
                let damaged = !segments.missing.is_empty() || !segments.corrupt.is_empty();
                let ids: Vec<_> = segments
                    .items
                    .iter()
                    .filter(|segment| !segment.id.is_empty())
                    .map(|segment| segment.id.clone())
                    .collect();
                if !ids.is_empty() {
                    let entry = anchored.entry(proof.file_id).or_insert_with(|| {
                        (
                            damaged,
                            Anchor {
                                proof: proof.clone(),
                                ids: ids.clone(),
                            },
                        )
                    });
                    ensure!(
                        entry.1.proof == *proof,
                        "stored repair descriptors conflict"
                    );
                    if damaged && !entry.0 {
                        *entry = (
                            true,
                            Anchor {
                                proof: proof.clone(),
                                ids,
                            },
                        );
                    }
                }
            }
            Ok(())
        };
        match &meta {
            FileMeta::Nzb(file) => add(&file.segments, &file.proof)?,
            FileMeta::Multipart(file) => {
                for part in &file.parts {
                    add(&part.segments, &part.proof)?;
                }
            }
        }
        let mut anchors: Vec<_> = anchored.into_values().collect();
        anchors.sort_by_key(|(damaged, _)| !damaged);
        anchors.truncate(64);
        let anchors = anchors.into_iter().map(|(_, anchor)| anchor).collect();
        Ok(Some(Input {
            nzb: id,
            nzb_hash: hash,
            posts,
            anchors,
        }))
    })
    .await
    .context("original repair NZB worker failed")?
}
pub(super) async fn current(
    db: &Db,
    id: ItemId,
    digest: [u8; 32],
    nzb: BlobId,
    nzb_hash: [u8; 32],
) -> anyhow::Result<bool> {
    let Some(scope) = scope(db, id).await? else {
        return Ok(false);
    };
    if scope.digest != digest || original_nzb(db, &scope.item).await? != Some(nzb) {
        return Ok(false);
    }
    let Some(raw) = db.blob_limited(nzb, MAX_METADATA).await? else {
        return Ok(false);
    };
    tokio::task::spawn_blocking(move || <[u8; 32]>::from(Sha256::digest(raw)) == nzb_hash)
        .await
        .context("repair source validation worker failed")
}
