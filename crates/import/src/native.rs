//! Provenance captured at conversion time, before C# metadata is transformed.
use infinidysk_core::FileMeta;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;

use crate::apply::CsFile;
use crate::csharp::{LongRange, MultipartFile};

/// Open only the original offline revision ledger. It is never created or upgraded.
pub struct Ledger(sqlx::SqlitePool);
impl Ledger {
    pub async fn open(path: &std::path::Path) -> Result<Self, sqlx::Error> {
        Ok(Self(
            sqlx::sqlite::SqlitePoolOptions::new()
                .max_connections(1)
                .connect_with(
                    sqlx::sqlite::SqliteConnectOptions::new()
                        .filename(path)
                        .read_only(true),
                )
                .await?,
        ))
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Source {
    pub version: u8,
    pub base_generation: String,
    pub dependencies: Vec<String>,
    pub fingerprint: Vec<u8>,
    pub size: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mapped_identity: Option<crate::article_identity::Evidence>,
}
impl Source {
    /// Hash the original compressed stream without buffering a second metadata copy.
    /// Draining after a successful parse also covers bytes read beyond known fields.
    pub(crate) fn capture_reader(
        input: impl std::io::Read,
        parse: impl FnOnce(&mut dyn std::io::Read) -> Result<CsFile, String>,
    ) -> Result<(CsFile, Self), String> {
        struct Hashed<R> {
            input: R,
            hash: Sha256,
        }
        impl<R: std::io::Read> std::io::Read for Hashed<R> {
            fn read(&mut self, bytes: &mut [u8]) -> std::io::Result<usize> {
                let n = self.input.read(bytes)?;
                self.hash.update(&bytes[..n]);
                Ok(n)
            }
        }
        let mut reader = Hashed {
            input,
            hash: Sha256::new(),
        };
        let file = parse(&mut reader)?;
        std::io::copy(&mut reader, &mut std::io::sink())
            .map_err(|e| format!("metadata blob: {e}"))?;
        let source = Self::capture_hash(&file, &reader.hash.finalize());
        Ok((file, source))
    }
    #[cfg(test)]
    fn capture(file: &CsFile, bytes: &[u8]) -> Self {
        Self::capture_hash(file, &Sha256::digest(bytes))
    }
    fn capture_hash(file: &CsFile, compressed_hash: &[u8]) -> Self {
        let mut dependencies = Vec::new();
        let mut add = |ids: &[String], fallbacks: Option<&[Vec<String>]>| {
            dependencies.extend(ids.iter().map(|id| upper(&Sha256::digest(id.as_bytes()))));
            dependencies.extend(
                fallbacks
                    .into_iter()
                    .flatten()
                    .flatten()
                    .map(|id| upper(&Sha256::digest(id.as_bytes()))),
            );
        };
        let base_generation = match file {
            CsFile::Nzb(file) => {
                add(&file.segment_ids, file.fallbacks.as_deref());
                format!("v2:{{blob}}:{}", upper(compressed_hash))
            }
            CsFile::Rar(file) => {
                for part in &file.parts {
                    add(&part.segment_ids, part.fallbacks.as_deref());
                }
                format!("v2:{{blob}}:{}", upper(compressed_hash))
            }
            CsFile::Multipart(file) => {
                for part in &file.parts {
                    add(&part.segment_ids, part.fallbacks.as_deref());
                }
                for part in &file.pending {
                    add(&part.segment_ids, part.fallbacks.as_deref());
                }
                let identity = file
                    .content_identity
                    .clone()
                    .filter(|s| !s.is_empty())
                    .unwrap_or_else(|| multipart_identity(file));
                format!("v3:{{blob}}:{identity}")
            }
        };
        Self {
            version: 1,
            base_generation,
            dependencies,
            fingerprint: vec![],
            size: 0,
            mapped_identity: None,
        }
    }
    pub(crate) fn finish(mut self, blob: &str, meta: &FileMeta) -> Result<String, String> {
        let blob = Uuid::parse_str(blob).map_err(|_| "invalid C# blob id")?;
        self.base_generation = self
            .base_generation
            .replace("{blob}", &blob.simple().to_string());
        self.fingerprint =
            Sha256::digest(meta.content_identity().map_err(|e| e.to_string())?).to_vec();
        self.size = meta.size();
        serde_json::to_string(&self).map_err(|e| e.to_string())
    }
    /// The repair revision ledger must be read from the same offline snapshot as the cache.
    pub async fn generation(&self, ledger: &Ledger) -> Result<String, sqlx::Error> {
        let revisions = &ledger.0;
        let epochs: Vec<String> = sqlx::query_scalar("SELECT Id FROM Identity LIMIT 2")
            .fetch_all(revisions)
            .await?;
        if epochs.len() != 1 {
            return Err(sqlx::Error::Protocol(
                "repair ledger identity is ambiguous".into(),
            ));
        }
        let mut hash = Sha256::new();
        hash.update(epochs[0].as_bytes());
        for chunk in self.dependencies.chunks(256) {
            // Bound both query count and memory; ordering and duplicate ids match C#.
            let mut query = sqlx::QueryBuilder::<sqlx::Sqlite>::new(
                "SELECT Hash,Stamp FROM Revisions WHERE Hash IN (",
            );
            let mut args = query.separated(",");
            for id in chunk {
                args.push_bind(id);
            }
            args.push_unseparated(")");
            let stamps: std::collections::HashMap<String, String> = query
                .build_query_as::<(String, String)>()
                .fetch_all(revisions)
                .await?
                .into_iter()
                .collect();
            for id in chunk {
                if let Some(stamp) = stamps.get(id) {
                    hash.update(id.as_bytes());
                    hash.update(stamp.as_bytes());
                }
            }
        }
        Ok(format!(
            "{}:{}",
            self.base_generation,
            upper(&hash.finalize())
        ))
    }
}
fn upper(bytes: &[u8]) -> String {
    use std::fmt::Write;
    bytes.iter().fold(String::new(), |mut out, byte| {
        write!(out, "{byte:02X}").expect("write to string");
        out
    })
}
struct Identity(Sha256);
impl Identity {
    fn number(&mut self, n: i64) {
        self.0.update(n.to_le_bytes());
    }
    fn bytes(&mut self, b: &[u8]) {
        self.number(i64::try_from(b.len()).expect("metadata fits i64"));
        self.0.update(b);
    }
    fn text(&mut self, text: Option<&str>) {
        match text {
            Some(s) => self.bytes(s.as_bytes()),
            None => self.number(-1),
        }
    }
    fn ids(&mut self, ids: &[String]) {
        self.number(i64::try_from(ids.len()).expect("metadata fits i64"));
        for id in ids {
            self.text(Some(id));
        }
    }
    fn range(&mut self, range: LongRange) {
        self.number(range.start);
        self.number(range.end);
    }
}
fn multipart_identity(file: &MultipartFile) -> String {
    let mut id = Identity(Sha256::new());
    id.text(Some("nzbdav:multipart-content:1"));
    id.text(file.path_in_archive.as_deref());
    id.number(file.expected_size.unwrap_or(-1));
    if let Some(aes) = &file.aes {
        id.text(Some("aes"));
        id.number(aes.decoded_size);
        id.bytes(&aes.iv);
        id.bytes(&aes.key);
    } else {
        id.text(Some("plain"));
    }
    id.number(i64::try_from(file.parts.len()).expect("metadata fits i64"));
    for part in &file.parts {
        id.ids(&part.segment_ids);
        id.range(part.segment_range);
        id.range(part.data_range);
        id.number(
            part.ranges
                .as_ref()
                .map_or(-1, |r| i64::try_from(r.len()).expect("metadata fits i64")),
        );
        for range in part.ranges.iter().flatten() {
            id.range(*range);
        }
    }
    id.number(i64::try_from(file.pending.len()).expect("metadata fits i64"));
    for part in &file.pending {
        id.ids(&part.segment_ids);
        id.range(part.segment_range);
    }
    upper(&id.0.finalize())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::csharp;

    #[test]
    fn streamed_provenance_hashes_the_full_input_after_a_short_parser_read() {
        let bytes = vec![17_u8; 128 * 1024 + 7];
        let (file, source) = Source::capture_reader(bytes.as_slice(), |reader| {
            let mut first = [0; 3];
            reader.read_exact(&mut first).map_err(|e| e.to_string())?;
            assert_eq!(first, [17; 3]);
            Ok(CsFile::Nzb(csharp::NzbFile::default()))
        })
        .unwrap();
        assert_eq!(
            source.base_generation,
            Source::capture(&file, &bytes).base_generation
        );
    }
    #[test]
    fn incomplete_provenance_reads_never_publish_a_source_generation() {
        use std::io::Read as _;
        struct Failed;
        impl std::io::Read for Failed {
            fn read(&mut self, _: &mut [u8]) -> std::io::Result<usize> {
                Err(std::io::Error::other("fixture input failure"))
            }
        }
        assert!(
            Source::capture_reader([0_u8; 3].as_slice().chain(Failed), |_| {
                Ok(CsFile::Nzb(csharp::NzbFile::default()))
            })
            .is_err()
        );
    }
    /// Run after the linked C# writer, using its receipt path. No provider is contacted.
    #[tokio::test]
    #[ignore = "requires the real 1.x C# writer fixture; see scripts/fixtures/legacy-native-cache"]
    async fn csharp_writer_generations_match_blob_multipart_and_repair_ledgers() {
        let path = std::env::var("INFINIDYSK_LEGACY_CACHE_FIXTURE").expect("fixture receipt path");
        let receipts: Vec<serde_json::Value> =
            serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
        for receipt in receipts {
            let dir = std::path::Path::new(receipt["metadata_path"].as_str().unwrap());
            let bytes = std::fs::read(dir.join("source.blob")).unwrap();
            let file = if receipt["multipart"].as_bool().unwrap() {
                let mut file = csharp::decode(bytes.as_slice(), csharp::multipart_file).unwrap();
                assert_eq!(
                    multipart_identity(&file),
                    receipt["content"].as_str().unwrap()
                );
                assert_eq!(
                    file.content_identity.as_deref(),
                    receipt["content"].as_str()
                );
                // Also prove the computed fallback, for blobs predating the saved identity.
                file.content_identity = None;
                CsFile::Multipart(file)
            } else {
                CsFile::Nzb(csharp::decode(bytes.as_slice(), csharp::nzb_file).unwrap())
            };
            let mut source = Source::capture(&file, &bytes);
            source.base_generation = source
                .base_generation
                .replace("{blob}", receipt["blob"].as_str().unwrap());
            let ledger = Ledger::open(&dir.join("repair-revisions.db"))
                .await
                .unwrap();
            assert_eq!(
                source.generation(&ledger).await.unwrap(),
                receipt["generation"].as_str().unwrap()
            );
        }
    }
}
