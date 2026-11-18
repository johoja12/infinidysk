//! Provenance captured at conversion time, before C# metadata is transformed.
use infinidysk_core::FileMeta;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use uuid::Uuid;

use crate::apply::CsFile;
use crate::csharp::{LongRange, MultipartFile};

/// Open only the original offline revision ledger. It is never created or upgraded.
pub struct Ledger {
    db: sqlx::SqlitePool,
    index: Option<RevisionIndex>,
}
// A stopped, checkpointed ledger cannot change for this reader's lifetime.
// Keep the fast path bounded; larger or unusual ledgers retain paged SQL reads.
const INDEX_ROWS: u16 = 4096;
struct RevisionIndex {
    epoch: String,
    stamps: HashMap<String, String>,
}
impl RevisionIndex {
    async fn load(db: &sqlx::SqlitePool) -> Result<Option<Self>, sqlx::Error> {
        // HashMap lookup must have the same equality as the original SQL.
        // Nonstandard affinities, composite keys or collations keep the SQL path.
        let schema: Option<String> = sqlx::query_scalar(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='Revisions' AND length(sql)<=4096"
        ).fetch_optional(db).await?;
        if schema
            .as_ref()
            .is_none_or(|sql| sql.to_ascii_uppercase().contains("COLLATE"))
        {
            return Ok(None);
        }
        let columns: Vec<(String, String, i64)> =
            sqlx::query_as("SELECT name,type,pk FROM pragma_table_info('Revisions')")
                .fetch_all(db)
                .await?;
        if columns.iter().filter(|(_, _, pk)| *pk != 0).count() != 1
            || !columns.iter().any(|(name, kind, pk)| {
                name == "Hash" && kind.eq_ignore_ascii_case("TEXT") && *pk == 1
            })
            || !columns
                .iter()
                .any(|(name, kind, _)| name == "Stamp" && kind.eq_ignore_ascii_case("TEXT"))
        {
            return Ok(None);
        }
        let epochs: Vec<Option<String>> = sqlx::query_scalar(
            "SELECT CASE WHEN length(Id)<=256 THEN Id END FROM Identity LIMIT 2",
        )
        .fetch_all(db)
        .await?;
        let [Some(epoch)] = epochs.as_slice() else {
            return Ok(None);
        };
        // Limit both row count and string length before allocating the index.
        let rows: Vec<(Option<String>, Option<String>)> = sqlx::query_as(
            "SELECT CASE WHEN length(Hash)<=256 THEN Hash END,CASE WHEN length(Stamp)<=256 THEN Stamp END FROM Revisions LIMIT $1"
        ).bind(i64::from(INDEX_ROWS) + 1).fetch_all(db).await?;
        if rows.len() > usize::from(INDEX_ROWS) {
            return Ok(None);
        }
        let mut stamps = HashMap::with_capacity(rows.len());
        for (id, stamp) in rows {
            let (Some(id), Some(stamp)) = (id, stamp) else {
                return Ok(None);
            };
            stamps.insert(id, stamp);
        }
        Ok(Some(Self {
            epoch: epoch.clone(),
            stamps,
        }))
    }
}
impl Ledger {
    pub async fn open(path: &std::path::Path) -> Result<Self, sqlx::Error> {
        Self::open_inner(path, false).await
    }
    /// In-place reuse requires a stopped, checkpointed source and creates no SQLite sidecars.
    pub async fn open_in_place(path: &std::path::Path) -> Result<Self, sqlx::Error> {
        let mut wal = path.as_os_str().to_os_string();
        wal.push("-wal");
        match std::fs::metadata(std::path::Path::new(&wal)) {
            Ok(meta) if meta.len() != 0 => {
                return Err(sqlx::Error::Protocol(
                    "legacy repair ledger must be stopped and WAL checkpointed".into(),
                ));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(sqlx::Error::Io(error)),
        }
        Self::open_inner(path, true).await
    }
    async fn open_inner(path: &std::path::Path, immutable: bool) -> Result<Self, sqlx::Error> {
        let db = sqlx::sqlite::SqlitePoolOptions::new()
            .max_connections(1)
            .connect_with(
                sqlx::sqlite::SqliteConnectOptions::new()
                    .filename(path)
                    .read_only(true)
                    .immutable(immutable),
            )
            .await?;
        // Indexing is optional. Schema/read errors still reach generation's
        // original validation path instead of inventing an empty ledger.
        let index = if immutable {
            RevisionIndex::load(&db).await.ok().flatten()
        } else {
            None
        };
        Ok(Self { db, index })
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
        let mut hash = Sha256::new();
        if let Some(index) = &ledger.index {
            hash.update(index.epoch.as_bytes());
            // Iterate the original dependency order, including duplicates, just
            // as the SQL path and original C# generation calculation do.
            for chunk in self.dependencies.chunks(256) {
                for id in chunk {
                    if let Some(stamp) = index.stamps.get(id) {
                        hash.update(id.as_bytes());
                        hash.update(stamp.as_bytes());
                    }
                }
                // Keep long dependency lists cooperative with playback tasks.
                tokio::task::yield_now().await;
            }
        } else {
            let revisions = &ledger.db;
            let epochs: Vec<String> = sqlx::query_scalar("SELECT Id FROM Identity LIMIT 2")
                .fetch_all(revisions)
                .await?;
            if epochs.len() != 1 {
                return Err(sqlx::Error::Protocol(
                    "repair ledger identity is ambiguous".into(),
                ));
            }
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
    struct TempLedger(std::path::PathBuf);
    impl Drop for TempLedger {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    async fn ledger_fixture(schema: &'static str) -> (TempLedger, sqlx::SqlitePool) {
        let dir = std::env::temp_dir().join(format!("id-ledger-index-{}", Uuid::new_v4()));
        std::fs::create_dir(&dir).unwrap();
        let db = sqlx::SqlitePool::connect_with(
            sqlx::sqlite::SqliteConnectOptions::new()
                .filename(dir.join("ledger.db"))
                .create_if_missing(true),
        )
        .await
        .unwrap();
        sqlx::raw_sql(schema).execute(&db).await.unwrap();
        (TempLedger(dir), db)
    }
    const LEDGER_SCHEMA: &str = "CREATE TABLE Identity(Id TEXT NOT NULL); INSERT INTO Identity VALUES('fixture-epoch'); CREATE TABLE Revisions(Hash TEXT PRIMARY KEY,Stamp TEXT NOT NULL);";
    fn source(dependencies: &[&str]) -> Source {
        Source {
            version: 1,
            base_generation: "fixture-base".into(),
            dependencies: dependencies.iter().map(|s| (*s).into()).collect(),
            fingerprint: vec![],
            size: 0,
            mapped_identity: None,
        }
    }
    #[tokio::test]
    async fn immutable_generation_index_matches_sql_for_order_duplicates_missing_and_empty_dependencies()
     {
        let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
        sqlx::query("INSERT INTO Revisions VALUES('a','stamp-a'),('b','stamp-b')")
            .execute(&db)
            .await
            .unwrap();
        db.close().await;
        let indexed = Ledger::open_in_place(&fixture.0.join("ledger.db"))
            .await
            .unwrap();
        let paged = Ledger::open(&fixture.0.join("ledger.db")).await.unwrap();
        assert_eq!(indexed.index.as_ref().unwrap().stamps.len(), 2);
        assert!(paged.index.is_none());
        for dependencies in [
            vec![],
            vec!["missing"],
            vec!["a", "b"],
            vec!["b", "a", "a", "missing"],
        ] {
            let source = source(&dependencies);
            assert_eq!(
                source.generation(&indexed).await.unwrap(),
                source.generation(&paged).await.unwrap()
            );
        }
        let mut expected = Sha256::new();
        for value in [
            "fixture-epoch",
            "b",
            "stamp-b",
            "a",
            "stamp-a",
            "a",
            "stamp-a",
        ] {
            expected.update(value.as_bytes());
        }
        assert_eq!(
            source(&["b", "a", "a", "missing"])
                .generation(&indexed)
                .await
                .unwrap(),
            format!("fixture-base:{}", upper(&expected.finalize()))
        );
    }
    #[tokio::test]
    async fn mutable_generation_reads_current_stamps_instead_of_using_an_index() {
        let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
        sqlx::query("INSERT INTO Revisions VALUES('a','first')")
            .execute(&db)
            .await
            .unwrap();
        let ledger = Ledger::open(&fixture.0.join("ledger.db")).await.unwrap();
        let source = source(&["a"]);
        let before = source.generation(&ledger).await.unwrap();
        sqlx::query("UPDATE Revisions SET Stamp='second'")
            .execute(&db)
            .await
            .unwrap();
        assert_ne!(before, source.generation(&ledger).await.unwrap());
        assert!(ledger.index.is_none());
    }
    #[tokio::test]
    async fn immutable_index_bounds_and_collations_fall_back_without_dropping_revisions() {
        for (schema, rows, stamps, expected_index) in [
            (LEDGER_SCHEMA, 4096, "stamp", true),
            (LEDGER_SCHEMA, 4097, "stamp", false),
            (LEDGER_SCHEMA, 1, "long", false),
            (
                "CREATE TABLE Identity(Id TEXT NOT NULL); INSERT INTO Identity VALUES('fixture-epoch'); CREATE TABLE Revisions(Hash TEXT COLLATE NOCASE PRIMARY KEY,Stamp TEXT NOT NULL);",
                1,
                "stamp",
                false,
            ),
        ] {
            let (fixture, db) = ledger_fixture(schema).await;
            let mut tx = db.begin().await.unwrap();
            for n in 0..rows {
                sqlx::query("INSERT INTO Revisions VALUES($1,$2)")
                    .bind(format!("HASH-{n}"))
                    .bind(if stamps == "long" {
                        "x".repeat(257)
                    } else {
                        stamps.into()
                    })
                    .execute(&mut *tx)
                    .await
                    .unwrap();
            }
            tx.commit().await.unwrap();
            db.close().await;
            let indexed = Ledger::open_in_place(&fixture.0.join("ledger.db"))
                .await
                .unwrap();
            let paged = Ledger::open(&fixture.0.join("ledger.db")).await.unwrap();
            assert_eq!(indexed.index.is_some(), expected_index);
            let last = format!("HASH-{}", rows - 1);
            for dependencies in [vec![last.as_str()], vec!["hash-0"]] {
                let source = source(&dependencies);
                assert_eq!(
                    source.generation(&indexed).await.unwrap(),
                    source.generation(&paged).await.unwrap()
                );
            }
        }
    }
    #[tokio::test]
    async fn ambiguous_or_missing_ledger_identity_never_gains_an_index_or_a_generation() {
        for identity in [
            "DELETE FROM Identity",
            "INSERT INTO Identity VALUES('another-epoch')",
        ] {
            let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
            sqlx::query(identity).execute(&db).await.unwrap();
            db.close().await;
            let ledger = Ledger::open_in_place(&fixture.0.join("ledger.db"))
                .await
                .unwrap();
            assert!(ledger.index.is_none());
            assert!(source(&["a"]).generation(&ledger).await.is_err());
        }
    }
    #[tokio::test]
    async fn empty_immutable_ledger_still_derives_the_identity_bound_generation() {
        let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
        db.close().await;
        let indexed = Ledger::open_in_place(&fixture.0.join("ledger.db"))
            .await
            .unwrap();
        assert!(indexed.index.as_ref().unwrap().stamps.is_empty());
        let mut expected = Sha256::new();
        expected.update(b"fixture-epoch");
        assert_eq!(
            source(&["absent"]).generation(&indexed).await.unwrap(),
            format!("fixture-base:{}", upper(&expected.finalize()))
        );
    }
    #[tokio::test]
    #[ignore = "explicit generated repair-ledger generation timing"]
    async fn generated_generation_index_timing() {
        let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
        let mut tx = db.begin().await.unwrap();
        for n in 0..883 {
            sqlx::query("INSERT INTO Revisions VALUES($1,'fixture-stamp')")
                .bind(format!("{:064X}", n + 1_000_000))
                .execute(&mut *tx)
                .await
                .unwrap();
        }
        tx.commit().await.unwrap();
        db.close().await;
        let indexed = Ledger::open_in_place(&fixture.0.join("ledger.db"))
            .await
            .unwrap();
        let paged = Ledger::open(&fixture.0.join("ledger.db")).await.unwrap();
        let mut source = source(&[]);
        source.dependencies = (0..125_405).map(|n| format!("{n:064X}")).collect();
        let expected = source.generation(&paged).await.unwrap();
        for (label, ledger) in [("paged", &paged), ("indexed", &indexed)] {
            let started = std::time::Instant::now();
            for _ in 0..5 {
                assert_eq!(source.generation(ledger).await.unwrap(), expected);
            }
            println!(
                "{label} generated source generation mean ms: {}",
                started.elapsed().as_millis() / 5
            );
        }
    }
    #[tokio::test]
    async fn indexed_generation_yields_to_other_runtime_work() {
        use std::sync::{
            Arc,
            atomic::{AtomicBool, Ordering},
        };
        let (fixture, db) = ledger_fixture(LEDGER_SCHEMA).await;
        db.close().await;
        let ledger = Ledger::open_in_place(&fixture.0.join("ledger.db"))
            .await
            .unwrap();
        let mut source = source(&[]);
        source.dependencies = vec!["absent".into(); 2048];
        let progressed = Arc::new(AtomicBool::new(false));
        let other = Arc::clone(&progressed);
        let task = tokio::spawn(async move {
            other.store(true, Ordering::Release);
        });
        source.generation(&ledger).await.unwrap();
        assert!(progressed.load(Ordering::Acquire));
        task.await.unwrap();
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
