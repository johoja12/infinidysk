//! Blob format for file metadata and original NZBs.
//!
//! ```text
//! offset  size  field
//! 0       3     magic "IDB"
//! 3       1     kind: 1 = file metadata, 2 = NZB
//! 4       2     schema version, little-endian u16
//! 6       ..    one zstd frame
//! ```
//!
//! The zstd payload of a metadata blob is [`FileMeta`] in the postcard wire format
//! (<https://postcard.jamesmunns.com/wire-format>): field order is the struct field order,
//! integers are varints, enums are a varint variant index. An NZB blob payload is the NZB
//! XML exactly as received.
//!
//! Postcard has no field tags, so any layout change needs a new version. Readers keep
//! decoding every older version (converting to the current types) instead of rewriting
//! old blobs, and may feature-gate on [`Blob::version`].
//!
//! Metadata versions: 1 = no encryption; 2 = adds [`MultipartFile::crypt`]; 3 = v2 layout
//! whose segments may have an empty id (an article the NZB never listed), so older readers
//! refuse it instead of asking providers for "". Writers emit the lowest version that holds
//! the data, so unencrypted files stay readable by v1 readers.

use serde::{Deserialize, Serialize};
use std::io::Read as _;

use crate::{FileMeta, FilePart, MultipartFile, NzbFile};

const MAGIC: &[u8; 3] = b"IDB";
const HEADER_LEN: usize = 6;
/// Current metadata layout.
pub const META_VERSION: u16 = 3;
/// Current NZB layout.
pub const NZB_VERSION: u16 = 1;
const ZSTD_LEVEL: i32 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Kind {
    Meta = 1,
    Nzb = 2,
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("not a blob (bad magic or truncated header)")]
    Header,
    #[error("unknown blob kind {0}")]
    UnknownKind(u8),
    #[error("expected a {expected:?} blob, found {found:?}")]
    WrongKind { expected: Kind, found: Kind },
    #[error("unsupported {kind:?} blob version {version}")]
    UnsupportedVersion { kind: Kind, version: u16 },
    #[error("blob exceeds its decoded size allowance")]
    TooLarge,
    #[error("zstd failed")]
    Zstd(#[from] std::io::Error),
    #[error("metadata payload is malformed")]
    Payload(#[from] postcard::Error),
}

/// A decoded header plus its still-compressed payload.
#[derive(Debug)]
pub struct Blob<'a> {
    pub kind: Kind,
    pub version: u16,
    payload: &'a [u8],
}

impl<'a> Blob<'a> {
    pub fn parse(bytes: &'a [u8]) -> Result<Self, Error> {
        let (header, payload) = bytes.split_at_checked(HEADER_LEN).ok_or(Error::Header)?;
        if &header[..3] != MAGIC {
            return Err(Error::Header);
        }
        let kind = match header[3] {
            1 => Kind::Meta,
            2 => Kind::Nzb,
            other => return Err(Error::UnknownKind(other)),
        };
        Ok(Self {
            kind,
            version: u16::from_le_bytes([header[4], header[5]]),
            payload,
        })
    }

    fn expect_limited(&self, kind: Kind, maximum: usize) -> Result<Vec<u8>, Error> {
        if self.kind != kind {
            return Err(Error::WrongKind {
                expected: kind,
                found: self.kind,
            });
        }
        let limit = u64::try_from(maximum)
            .ok()
            .and_then(|n| n.checked_add(1))
            .ok_or(Error::TooLarge)?;
        let mut output = Vec::with_capacity(maximum.min(64 * 1024));
        let mut decoder = zstd::stream::read::Decoder::new(self.payload)?;
        // Bound the decoder's history as well as returned bytes. Output limits
        // alone do not stop a small frame from requesting a huge zstd window.
        // Existing streaming writers declare a window larger than tiny payloads.
        // Keep an 8 MB-scale floor for their frames, independently of output size.
        let window = maximum.saturating_sub(1).bit_width().clamp(23, 31);
        decoder.window_log_max(window)?;
        decoder.take(limit).read_to_end(&mut output)?;
        if output.len() > maximum {
            return Err(Error::TooLarge);
        }
        Ok(output)
    }
    fn expect(&self, kind: Kind) -> Result<Vec<u8>, Error> {
        if self.kind != kind {
            return Err(Error::WrongKind {
                expected: kind,
                found: self.kind,
            });
        }
        Ok(zstd::decode_all(self.payload)?)
    }
}

/// Version 1 layout: [`FileMeta`] before multipart encryption.
#[derive(Serialize, Deserialize)]
enum MetaV1 {
    Nzb(NzbFile),
    Multipart { parts: Vec<FilePart> },
}

pub fn encode_meta(meta: &FileMeta) -> Result<Vec<u8>, Error> {
    if meta.has_placeholders() {
        return encode(Kind::Meta, META_VERSION, &postcard::to_stdvec(meta)?);
    }
    let v1 = match meta {
        FileMeta::Nzb(f) => Some(MetaV1::Nzb(f.clone())),
        FileMeta::Multipart(MultipartFile { parts, crypt: None }) => Some(MetaV1::Multipart {
            parts: parts.clone(),
        }),
        FileMeta::Multipart(_) => None,
    };
    match v1 {
        Some(v1) => encode(Kind::Meta, 1, &postcard::to_stdvec(&v1)?),
        None => encode(Kind::Meta, 2, &postcard::to_stdvec(meta)?),
    }
}

pub fn decode_meta(bytes: &[u8]) -> Result<FileMeta, Error> {
    let blob = Blob::parse(bytes)?;
    let payload = blob.expect(Kind::Meta)?;
    decode_meta_payload(blob.version, &payload)
}
fn decode_meta_payload(version: u16, payload: &[u8]) -> Result<FileMeta, Error> {
    match version {
        1 => Ok(match postcard::from_bytes(payload)? {
            MetaV1::Nzb(f) => FileMeta::Nzb(f),
            MetaV1::Multipart { parts } => {
                FileMeta::Multipart(MultipartFile { parts, crypt: None })
            }
        }),
        2 | 3 => Ok(postcard::from_bytes(payload)?),
        version => Err(Error::UnsupportedVersion {
            kind: Kind::Meta,
            version,
        }),
    }
}

/// Bounded metadata decoding for background repair/import workflows. Future layouts
/// and foreign kinds are rejected before decompressing their payload.
pub fn decode_meta_limited(bytes: &[u8], maximum: usize) -> Result<FileMeta, Error> {
    let blob = Blob::parse(bytes)?;
    if blob.kind != Kind::Meta {
        return Err(Error::WrongKind {
            expected: Kind::Meta,
            found: blob.kind,
        });
    }
    if !(1..=META_VERSION).contains(&blob.version) {
        return Err(Error::UnsupportedVersion {
            kind: Kind::Meta,
            version: blob.version,
        });
    }
    let payload = blob.expect_limited(Kind::Meta, maximum)?;
    decode_meta_payload(blob.version, &payload)
}
/// Bounded original-NZB decoding. The bytes are preserved exactly; parsing and any
/// source authorization remain the caller's responsibility.
pub fn decode_nzb_limited(bytes: &[u8], maximum: usize) -> Result<Vec<u8>, Error> {
    let blob = Blob::parse(bytes)?;
    if blob.kind != Kind::Nzb {
        return Err(Error::WrongKind {
            expected: Kind::Nzb,
            found: blob.kind,
        });
    }
    if blob.version != NZB_VERSION {
        return Err(Error::UnsupportedVersion {
            kind: Kind::Nzb,
            version: blob.version,
        });
    }
    blob.expect_limited(Kind::Nzb, maximum)
}

pub fn encode_nzb(xml: &[u8]) -> Result<Vec<u8>, Error> {
    encode(Kind::Nzb, NZB_VERSION, xml)
}

pub fn decode_nzb(bytes: &[u8]) -> Result<Vec<u8>, Error> {
    let blob = Blob::parse(bytes)?;
    let payload = blob.expect(Kind::Nzb)?;
    match blob.version {
        1 => Ok(payload),
        version => Err(Error::UnsupportedVersion {
            kind: Kind::Nzb,
            version,
        }),
    }
}

fn encode(kind: Kind, version: u16, payload: &[u8]) -> Result<Vec<u8>, Error> {
    let mut out = Vec::with_capacity(HEADER_LEN + payload.len() / 4);
    out.extend_from_slice(MAGIC);
    out.push(kind as u8);
    out.extend_from_slice(&version.to_le_bytes());
    zstd::stream::copy_encode(payload, &mut out, ZSTD_LEVEL)?;
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::crypt::{Crypt, Key};
    use crate::{ByteRange, Par2Proof, Segment, Segments};

    fn segments(n: u32) -> Segments {
        Segments {
            items: (0..n)
                .map(|i| Segment {
                    id: format!("part{i}of{n}.abc@example"),
                    size: 768_000,
                    fallbacks: if i == 1 {
                        vec!["alt@example".into()]
                    } else {
                        vec![]
                    },
                })
                .collect(),
            sizes_trusted: true,
            missing: vec![2],
            corrupt: vec![],
        }
    }

    #[test]
    fn metadata_round_trips_and_pins_its_header() {
        let metas = [
            FileMeta::Nzb(NzbFile {
                segments: segments(4),
                proof: Some(Par2Proof {
                    file_length: 3_072_000,
                    slice_size: 768_000,
                    slice_md5: vec![[7; 16]; 4],
                    slice_crc32: vec![1, 2, 3, 4],
                    file_id: [1; 16],
                    file_md5: [2; 16],
                    md5_16k: [3; 16],
                }),
            }),
            FileMeta::Multipart(MultipartFile {
                parts: vec![FilePart {
                    segments: segments(3),
                    offset: 768_000,
                    data: ByteRange {
                        start: 768_100,
                        end: 3_072_000,
                    },
                    split_after: true,
                    proof: None,
                }],
                crypt: None,
            }),
        ];
        for meta in metas {
            let bytes = encode_meta(&meta).expect("encodes");
            assert_eq!(
                &bytes[..6],
                b"IDB\x01\x01\x00",
                "header layout is part of the format"
            );
            assert_eq!(decode_meta(&bytes).expect("decodes"), meta);
        }
        let encrypted = FileMeta::Multipart(MultipartFile {
            parts: vec![],
            crypt: Some(Crypt {
                key: Key::Aes256([5; 32]),
                iv: [6; 16],
                size: 37,
            }),
        });
        let bytes = encode_meta(&encrypted).expect("encodes");
        assert_eq!(&bytes[..6], b"IDB\x01\x02\x00", "encryption needs v2");
        assert_eq!(decode_meta(&bytes).expect("decodes"), encrypted);
        assert_eq!(encrypted.size(), 37);

        let mut gap = segments(4);
        gap.items[2].id.clear();
        let gap = FileMeta::Nzb(NzbFile {
            segments: gap,
            proof: None,
        });
        let bytes = encode_meta(&gap).expect("encodes");
        assert_eq!(&bytes[..6], b"IDB\x01\x03\x00", "placeholders need v3");
        assert_eq!(decode_meta(&bytes).expect("decodes"), gap);
    }

    #[test]
    fn readers_reject_foreign_kinds_and_future_versions() {
        let nzb = encode_nzb(b"<nzb/>").expect("encodes");
        assert_eq!(decode_nzb(&nzb).expect("decodes"), b"<nzb/>");
        assert!(matches!(decode_meta(&nzb), Err(Error::WrongKind { .. })));

        let mut future = nzb;
        future[4] = 9;
        assert!(matches!(
            decode_nzb(&future),
            Err(Error::UnsupportedVersion { version: 9, .. })
        ));
        assert!(matches!(decode_meta(b"ID"), Err(Error::Header)));
    }
    #[test]
    fn bounded_readers_stop_compressed_expansion_and_keep_kind_version_guards() {
        let plain = vec![b'x'; 100_000];
        let nzb = encode_nzb(&plain).expect("encode");
        assert!(
            nzb.len() < 1000,
            "a small compressed frame can expand far beyond a repair metadata allowance"
        );
        assert!(matches!(
            decode_nzb_limited(&nzb, 99_999),
            Err(Error::TooLarge)
        ));
        assert_eq!(
            decode_nzb_limited(&nzb, 100_000).expect("exact allowance"),
            plain
        );
        assert!(matches!(
            decode_meta_limited(&nzb, 100_000),
            Err(Error::WrongKind { .. })
        ));
        let mut future = nzb;
        future[4] = 9;
        future.truncate(6);
        assert!(
            matches!(
                decode_nzb_limited(&future, 0),
                Err(Error::UnsupportedVersion { version: 9, .. })
            ),
            "future layouts fail before decompression"
        );
        let meta = FileMeta::Nzb(NzbFile {
            segments: segments(4),
            proof: None,
        });
        let encoded = encode_meta(&meta).expect("metadata");
        assert!(matches!(
            decode_meta_limited(&encoded, 1),
            Err(Error::TooLarge)
        ));
        assert_eq!(
            decode_meta_limited(&encoded, 1 << 20).expect("bounded metadata"),
            meta
        );
    }
}
