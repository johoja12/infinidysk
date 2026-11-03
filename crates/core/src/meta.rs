use serde::{Deserialize, Serialize};

use crate::ByteRange;
use crate::crypt::Crypt;

/// Streaming metadata for one file, stored as a [`crate::blob`].
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum FileMeta {
    Nzb(NzbFile),
    Multipart(MultipartFile),
}

impl FileMeta {
    /// Decoded file size in bytes.
    #[must_use]
    pub fn size(&self) -> u64 {
        match self {
            Self::Nzb(f) => f.segments.size(),
            Self::Multipart(f) => f.size(),
        }
    }

    /// Canonical content-defining metadata for cache identity. Availability and
    /// verification annotations do not change posted bytes or the archive member.
    pub fn content_identity(&self) -> Result<Vec<u8>, crate::blob::Error> {
        #[derive(Serialize)]
        struct SourceSegment<'a> {
            id: &'a str,
            size: u32,
        }
        #[derive(Serialize)]
        struct SourcePart<'a> {
            segments: Vec<SourceSegment<'a>>,
            offset: u64,
            data: &'a ByteRange,
        }
        #[derive(Serialize)]
        enum Source<'a> {
            Nzb(Vec<SourceSegment<'a>>),
            Multipart(Vec<SourcePart<'a>>, Option<&'a Crypt>),
        }
        fn segments(s: &Segments) -> Vec<SourceSegment<'_>> {
            s.items
                .iter()
                .map(|s| SourceSegment {
                    id: &s.id,
                    size: s.size,
                })
                .collect()
        }
        let source = match self {
            Self::Nzb(file) => Source::Nzb(segments(&file.segments)),
            Self::Multipart(file) => Source::Multipart(
                file.parts
                    .iter()
                    .map(|p| SourcePart {
                        segments: segments(&p.segments),
                        offset: p.offset,
                        data: &p.data,
                    })
                    .collect(),
                file.crypt.as_ref(),
            ),
        };
        // Version the cache identity independently from persisted metadata blobs.
        Ok(postcard::to_stdvec(&(1_u8, source))?)
    }

    /// Whether reads go through AES.
    #[must_use]
    pub const fn encrypted(&self) -> bool {
        matches!(self, Self::Multipart(MultipartFile { crypt: Some(_), .. }))
    }

    /// Whether a segment has an empty id: an article the NZB never listed.
    #[must_use]
    pub fn has_placeholders(&self) -> bool {
        let any = |s: &Segments| s.items.iter().any(|s| s.id.is_empty());
        match self {
            Self::Nzb(f) => any(&f.segments),
            Self::Multipart(f) => f.parts.iter().any(|p| any(&p.segments)),
        }
    }
}

/// A file that is exactly the decoded segments of one NZB file entry.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct NzbFile {
    pub segments: Segments,
    pub proof: Option<Par2Proof>,
}

/// A file stitched from byte ranges of several volumes, in order.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct MultipartFile {
    pub parts: Vec<FilePart>,
    /// Set when the parts hold AES ciphertext (blob version 2).
    pub crypt: Option<Crypt>,
}

impl MultipartFile {
    /// Plaintext size in bytes.
    #[must_use]
    pub fn size(&self) -> u64 {
        match &self.crypt {
            Some(c) => c.size,
            None => self.parts.iter().map(|p| p.data.len()).sum(),
        }
    }
}

/// The slice of one volume that belongs to a multipart file.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct FilePart {
    /// The volume segments that cover [`Self::data`], not necessarily the whole volume.
    pub segments: Segments,
    /// Volume offset where the first of [`Self::segments`] starts.
    pub offset: u64,
    /// The member's bytes, as volume offsets inside the segments' span.
    pub data: ByteRange,
    /// The member continues in the next volume.
    pub split_after: bool,
    /// PAR2 entry of the whole volume.
    pub proof: Option<Par2Proof>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Segments {
    pub items: Vec<Segment>,
    /// Sizes come from validated yEnc part headers, so seeks may compute offsets
    /// arithmetically instead of probing.
    pub sizes_trusted: bool,
    /// Ascending indices missing on every provider.
    pub missing: Vec<u32>,
    /// Ascending indices that decoded corrupt on every provider.
    pub corrupt: Vec<u32>,
}

impl Segments {
    #[must_use]
    pub fn size(&self) -> u64 {
        self.items.iter().map(|s| u64::from(s.size)).sum()
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Segment {
    /// Message-ID without angle brackets.
    pub id: String,
    /// Decoded size in bytes.
    pub size: u32,
    /// Alternate message-ids carried over from imported metadata. Neither playback nor
    /// health reads them; a segment is judged by `id` alone.
    pub fallbacks: Vec<String>,
}

/// PAR2 description of a whole file, enough to verify it slice by slice.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Par2Proof {
    pub file_length: u64,
    pub slice_size: u32,
    pub slice_md5: Vec<[u8; 16]>,
    pub slice_crc32: Vec<u32>,
    pub file_id: [u8; 16],
    pub file_md5: [u8; 16],
    pub md5_16k: [u8; 16],
}

#[cfg(test)]
mod content_identity_tests {
    use super::*;
    fn file() -> FileMeta {
        FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: "fixture@loopback".to_owned(),
                    size: 100,
                    fallbacks: vec![],
                }],
                ..Segments::default()
            },
            proof: None,
        })
    }
    #[test]
    fn availability_annotations_do_not_discard_good_cached_bytes() {
        let original = file();
        let mut annotated = original.clone();
        let FileMeta::Nzb(f) = &mut annotated else {
            unreachable!()
        };
        f.segments.missing = vec![0];
        f.segments.corrupt = vec![0];
        f.segments.sizes_trusted = true;
        f.segments.items[0]
            .fallbacks
            .push("unused@loopback".to_owned());
        assert_eq!(
            original.content_identity().expect("encode"),
            annotated.content_identity().expect("encode")
        );
    }
    #[test]
    fn different_sources_or_geometry_change_cache_identity() {
        let original = file();
        let mut changed = original.clone();
        let FileMeta::Nzb(f) = &mut changed else {
            unreachable!()
        };
        f.segments.items[0].size += 1;
        assert_ne!(
            original.content_identity().expect("encode"),
            changed.content_identity().expect("encode")
        );
        let FileMeta::Nzb(f) = &mut changed else {
            unreachable!()
        };
        f.segments.items[0].size -= 1;
        f.segments.items[0].id = "replacement@loopback".to_owned();
        assert_ne!(
            original.content_identity().expect("encode"),
            changed.content_identity().expect("encode")
        );
    }
}
