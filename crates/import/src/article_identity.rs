//! Exact 1.x mapped-export article identities. Original NZB order, wire-byte counts
//! and optional ordinals matter; the SAB-normalized queue parser is not equivalent.
use crate::apply::CsFile;
use quick_xml::{Reader, events::Event};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use std::collections::HashMap;
use std::fmt::Write as _;

#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct Error(&'static str);
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct Evidence {
    pub version: u8,
    pub kind: String,
    pub digest: String,
    pub payload_sha256: String,
    pub size: u64,
}
#[derive(Debug)]
struct Segment {
    number: Option<i32>,
    bytes: i64,
    id: String,
}
#[derive(Debug)]
pub struct Document {
    files: Vec<Vec<Segment>>,
    index: HashMap<String, (usize, usize)>,
    payload_sha256: String,
}
fn lookup(id: &str) -> &str {
    id.trim()
        .trim_start_matches('<')
        .trim_end_matches('>')
        .trim()
}
fn message(id: &str) -> Result<&str, Error> {
    let id = id.trim();
    let id = if id.starts_with('<') && id.ends_with('>') && id.len() >= 2 {
        id[1..id.len() - 1].trim()
    } else {
        id
    };
    if id.is_empty() {
        Err(Error("empty source article identity"))
    } else {
        Ok(id)
    }
}
fn hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .fold(String::with_capacity(bytes.len() * 2), |mut out, b| {
            write!(out, "{b:02x}").expect("formatting into String cannot fail");
            out
        })
}
fn text(hash: &mut Sha256, value: &str) -> Result<(), Error> {
    hash.update(
        i32::try_from(value.len())
            .map_err(|_| Error("source identity string exceeds bound"))?
            .to_be_bytes(),
    );
    hash.update(value);
    Ok(())
}
fn begin(domain: &str) -> Result<Sha256, Error> {
    let mut hash = Sha256::new();
    text(&mut hash, domain)?;
    Ok(hash)
}
fn segments(hash: &mut Sha256, values: &[&Segment]) -> Result<(), Error> {
    if values.is_empty() {
        return Err(Error("source identity has no articles"));
    }
    hash.update(
        i32::try_from(values.len())
            .map_err(|_| Error("source identity article count exceeds bound"))?
            .to_be_bytes(),
    );
    for segment in values {
        if segment.bytes < 0 {
            return Err(Error("source article has a negative wire-byte count"));
        }
        hash.update(segment.number.unwrap_or(-1).to_be_bytes());
        hash.update(segment.bytes.to_be_bytes());
        text(hash, message(&segment.id)?)?;
    }
    Ok(())
}
fn range(start: i64, len: i64) -> Result<(), Error> {
    if start < 0 || len < 0 || start.checked_add(len).is_none() {
        return Err(Error("source archive range is invalid"));
    }
    Ok(())
}
/// Parses without sorting, deduplicating or replacing original XML ordinals.
/// Unsupported encodings/structures remain unknown instead of guessed identities.
pub fn parse(xml: &[u8]) -> Result<Document, Error> {
    if xml.len() as u64 > crate::MAX_NZB_BLOB_BYTES {
        return Err(Error("source NZB exceeds bound"));
    }
    let xml =
        std::str::from_utf8(xml).map_err(|_| Error("source NZB mapping requires UTF-8 XML"))?;
    let mut reader = Reader::from_str(xml);
    let mut files = Vec::new();
    let mut file: Option<Vec<Segment>> = None;
    let mut in_segments = false;
    let mut current = None;
    let mut raw = String::new();
    let mut count = 0_u64;
    loop {
        match reader
            .read_event()
            .map_err(|_| Error("source NZB mapping XML is invalid"))?
        {
            Event::Start(_) | Event::Empty(_) if current.is_some() => {
                return Err(Error("nested source article text is invalid"));
            }
            Event::Start(e) => match e.name().as_ref() {
                "file" => {
                    if file.is_some() {
                        return Err(Error("nested source NZB files are ambiguous"));
                    }
                    file = Some(Vec::new());
                }
                "segments" if file.is_some() => {
                    if in_segments {
                        return Err(Error("nested source segments are ambiguous"));
                    }
                    in_segments = true;
                }
                "segment" if in_segments => {
                    if current.is_some() {
                        return Err(Error("nested source articles are ambiguous"));
                    }
                    let mut number = None;
                    let mut bytes = 0;
                    for attribute in e.attributes() {
                        let a = attribute
                            .map_err(|_| Error("source article attributes are invalid"))?;
                        let value = a
                            .normalized_value(quick_xml::XmlVersion::Implicit1_0)
                            .map_err(|_| Error("source article attributes are invalid"))?;
                        match a.key.as_ref() {
                            "number" => number = value.trim().parse().ok(),
                            "bytes" => bytes = value.trim().parse().unwrap_or(0),
                            _ => {}
                        }
                    }
                    raw.clear();
                    current = Some((number, bytes));
                }
                _ => {}
            },
            Event::Text(t) if current.is_some() => raw.push_str(&t.xml10_content()),
            Event::CData(t) if current.is_some() => {
                raw.push_str(&quick_xml::escape::escape(t.xml10_content()));
            }
            Event::GeneralRef(r) if current.is_some() => {
                raw.push('&');
                raw.push_str(&r.xml10_content());
                raw.push(';');
            }
            Event::End(e) => match e.name().as_ref() {
                "segment" => {
                    if let Some((number, bytes)) = current.take() {
                        let id = quick_xml::escape::unescape(&raw)
                            .map_err(|_| Error("source article entity is invalid"))?
                            .trim()
                            .to_owned();
                        if id.len() > 8192 {
                            return Err(Error("source article identity exceeds bound"));
                        }
                        count += 1;
                        if count > 2_000_000 {
                            return Err(Error("source article count exceeds bound"));
                        }
                        file.as_mut()
                            .ok_or(Error("source article has no file"))?
                            .push(Segment { number, bytes, id });
                    }
                }
                "segments" => in_segments = false,
                "file" => {
                    if let Some(value) = file.take() {
                        files.push(value);
                    }
                }
                _ => {}
            },
            Event::Empty(e) if e.name().as_ref() == "file" => {
                if file.is_some() {
                    return Err(Error("nested source NZB files are ambiguous"));
                }
                files.push(Vec::new());
            }
            Event::Empty(e) if e.name().as_ref() == "segment" && in_segments => {
                return Err(Error("empty source article identity"));
            }
            Event::Eof => break,
            _ => {}
        }
        if files.len() > 100_000 {
            return Err(Error("source NZB file count exceeds bound"));
        }
        if raw.len() > 64 * 1024 {
            return Err(Error("source article text exceeds bound"));
        }
    }
    if file.is_some() || current.is_some() || files.is_empty() {
        return Err(Error("source NZB mapping is incomplete"));
    }
    let mut index = HashMap::new();
    for (f, values) in files.iter().enumerate() {
        for (s, segment) in values.iter().enumerate() {
            let key = lookup(&segment.id);
            if key.is_empty() || index.insert(key.to_owned(), (f, s)).is_some() {
                return Err(Error("source NZB article identity is empty or ambiguous"));
            }
        }
    }
    Ok(Document {
        files,
        index,
        payload_sha256: hex(&Sha256::digest(xml.as_bytes())),
    })
}
impl Document {
    fn resolve(&self, ids: &[String]) -> Result<Vec<&Segment>, Error> {
        ids.iter()
            .map(|id| {
                self.index
                    .get(lookup(id))
                    .map(|&(f, s)| &self.files[f][s])
                    .ok_or(Error("source metadata article is absent from its NZB"))
            })
            .collect()
    }
    pub fn direct(&self, ids: &[String]) -> Result<String, Error> {
        let mut hash = begin("nzbdav-direct-v1")?;
        segments(&mut hash, &self.resolve(ids)?)?;
        Ok(hex(&hash.finalize()))
    }
    pub fn release(&self) -> Result<String, Error> {
        let mut hash = begin("nzbdav-release-v1")?;
        hash.update(
            i32::try_from(self.files.len())
                .map_err(|_| Error("source NZB file count exceeds bound"))?
                .to_be_bytes(),
        );
        for file in &self.files {
            text(&mut hash, "file")?;
            segments(&mut hash, &file.iter().collect::<Vec<_>>())?;
        }
        Ok(hex(&hash.finalize()))
    }
    pub(crate) fn capture(&self, file: &CsFile, size: u64) -> Result<Evidence, Error> {
        let (kind, digest) = match file {
            CsFile::Nzb(file) => ("direct-articles-v1", self.direct(&file.segment_ids)?),
            CsFile::Rar(file) => {
                let parts = file
                    .parts
                    .iter()
                    .map(|p| {
                        let end = p
                            .offset
                            .checked_add(p.byte_count)
                            .ok_or(Error("source RAR range overflowed"))?;
                        Ok((
                            &p.segment_ids[..],
                            0,
                            p.part_size.max(end),
                            p.offset,
                            p.byte_count,
                        ))
                    })
                    .collect::<Result<Vec<_>, Error>>()?;
                ("archive-articles-v2", self.archive(&parts, size)?)
            }
            CsFile::Multipart(file) => {
                if !file.pending.is_empty() {
                    return Err(Error("source archive still has unresolved parts"));
                }
                let parts = file
                    .parts
                    .iter()
                    .map(|p| {
                        let segment_len = p
                            .segment_range
                            .end
                            .checked_sub(p.segment_range.start)
                            .ok_or(Error("source segment range overflowed"))?;
                        let file_len = p
                            .data_range
                            .end
                            .checked_sub(p.data_range.start)
                            .ok_or(Error("source member range overflowed"))?;
                        Ok((
                            &p.segment_ids[..],
                            p.segment_range.start,
                            segment_len,
                            p.data_range.start,
                            file_len,
                        ))
                    })
                    .collect::<Result<Vec<_>, Error>>()?;
                ("archive-articles-v2", self.archive(&parts, size)?)
            }
        };
        Ok(Evidence {
            version: 1,
            kind: kind.into(),
            digest,
            payload_sha256: self.payload_sha256.clone(),
            size,
        })
    }
    fn archive(
        &self,
        parts: &[(&[String], i64, i64, i64, i64)],
        size: u64,
    ) -> Result<String, Error> {
        if parts.is_empty() {
            return Err(Error("source archive identity has no parts"));
        }
        let mut hash = begin("nzbdav-archive-articles-v2")?;
        text(&mut hash, &self.release()?)?;
        hash.update(
            i32::try_from(parts.len())
                .map_err(|_| Error("source archive part count exceeds bound"))?
                .to_be_bytes(),
        );
        for (ids, start, len, file_start, file_len) in parts {
            range(*start, *len)?;
            range(*file_start, *file_len)?;
            text(&mut hash, "part")?;
            segments(&mut hash, &self.resolve(ids)?)?;
            for value in [start, len, file_start, file_len] {
                hash.update(value.to_be_bytes());
            }
        }
        hash.update(
            i64::try_from(size)
                .map_err(|_| Error("source archive size exceeds bound"))?
                .to_be_bytes(),
        );
        Ok(hex(&hash.finalize()))
    }
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Vectors emitted by the linked original C# identity implementation.
mod tests {
    use super::*;
    const XML:&[u8]=br#"<nzb><file><segments><segment number="2" bytes="7"> &lt;fixture@loopback&gt; </segment><segment bytes="11">tail@loopback</segment></segments></file></nzb>"#;
    #[test]
    fn original_csharp_vectors_preserve_xml_order_missing_ordinals_and_wire_sizes() {
        let document = parse(XML).unwrap();
        let ids = vec!["fixture@loopback".into(), "tail@loopback".into()];
        assert_eq!(
            document.direct(&ids).unwrap(),
            "bcc386cd57257f4e833886556dc7febe55787676cb32d09cd92ec29635c3ef76"
        );
        assert_eq!(
            document.release().unwrap(),
            "6b1e7b33094491509fb199f9634033a204f780dc6539e71c106363025cb3c61a"
        );
        let archive = document.archive(&[(&ids, 0, 18, 4, 10)], 10).unwrap();
        assert_eq!(
            archive,
            "1bb884413c75da816cff9b7033c7caa0750f0c003d524e0cf65cd5e5efc87ae0"
        );
        let rar = CsFile::Rar(crate::csharp::RarFile {
            parts: vec![crate::csharp::RarPart {
                segment_ids: ids.clone(),
                part_size: 18,
                offset: 4,
                byte_count: 10,
                ..Default::default()
            }],
            ..Default::default()
        });
        assert_eq!(document.capture(&rar, 10).unwrap().digest, archive);
        let member = CsFile::Multipart(crate::csharp::MultipartFile {
            parts: vec![crate::csharp::FilePart {
                segment_ids: ids.clone(),
                segment_range: crate::csharp::LongRange { start: 0, end: 18 },
                data_range: crate::csharp::LongRange { start: 4, end: 14 },
                ..Default::default()
            }],
            ..Default::default()
        });
        assert_eq!(document.capture(&member, 10).unwrap().digest, archive);
        assert_ne!(
            document
                .direct(&ids.into_iter().rev().collect::<Vec<_>>())
                .unwrap(),
            "bcc386cd57257f4e833886556dc7febe55787676cb32d09cd92ec29635c3ef76"
        );
    }
    #[test]
    fn ambiguous_missing_negative_and_overflowing_metadata_never_supply_a_mapping() {
        for xml in [br#"<nzb><file><segments><segment bytes="1">same</segment><segment bytes="1">&lt;same&gt;</segment></segments></file></nzb>"#.as_slice(),br#"<nzb><file><segments><segment bytes="1"></segment></segments></file></nzb>"#.as_slice(),b"<nzb><file>"] {assert!(parse(xml).is_err());}
        let document = parse(XML).unwrap();
        assert!(document.direct(&["missing@loopback".into()]).is_err());
        let ids = vec!["fixture@loopback".into()];
        assert!(document.archive(&[(&ids, i64::MAX, 1, 0, 1)], 1).is_err());
        assert!(document.archive(&[(&ids, 0, 1, -1, 1)], 1).is_err());
        let negative=parse(br#"<nzb><file><segments><segment bytes="-1">fixture@loopback</segment></segments></file></nzb>"#).unwrap();
        assert!(negative.direct(&ids).is_err());
    }
}
