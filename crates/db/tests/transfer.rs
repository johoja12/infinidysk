//! The SQLite → PostgreSQL copy. Tests that need a server run when
//! `INFINIDYSK_TEST_POSTGRES_URL` points at one whose user may create databases.

use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use infinidysk_db::transfer::{self, Capture, PgConnection};
use infinidysk_db::{
    Conversion, Db, Handoff, HealthCheck, HealthResult, HistoryEntry, Item, JobStatus, QueueEntry,
    Repair,
};
use sqlx::{AssertSqlSafe, SqlitePool};
use std::path::PathBuf;
use std::time::Duration;

/// Every table, as the copy lists them.
const ALL: [&str; 38] = [
    "blobs",
    "items",
    "queue",
    "history",
    "health_checks",
    "media_probes",
    "rejected_releases",
    "rejected_articles",
    "arr_refresh",
    "search_reservations",
    "arr_owners",
    "arr_events",
    "config",
    "accounts",
    "handoff_files",
    "library_files",
    "completed_link_removals",
    "import_sources",
    "health_reader_holds",
    "urgent_health",
    "regrab_intents",
    "library_catalogue",
    "library_links",
    "plex_classification_snapshot",
    "content_sweep_state",
    "content_sweep_items",
    "repair_lineage",
    "repair_observations",
    "par2_attempt_state",
    "par2_attempts",
    "prefetch_wire_budget",
    "prefetch_history",
    "library_scan",
    "library_scan_dirs",
    "library_scan_seen",
    "library_scan_links",
    "migration_submissions",
    "legacy_repair_holds",
];

struct Sqlite {
    db: Db,
    raw: SqlitePool,
    path: PathBuf,
}

impl Sqlite {
    async fn open() -> Self {
        let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
        let url = format!("sqlite:{}", path.display());
        let db = Db::connect(&url).await.expect("connect");
        let raw = SqlitePool::connect(&url).await.expect("open");
        Self { db, raw, path }
    }

    async fn close(self) {
        self.raw.close().await;
        self.db.close().await;
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", self.path.display()));
        }
    }

    async fn exec(&self, sql: &str) {
        sqlx::raw_sql(AssertSqlSafe(sql.to_owned()))
            .execute(&self.raw)
            .await
            .expect(sql);
    }

    /// Tables with a row in the change table.
    async fn logged(&self) -> Vec<String> {
        sqlx::query_scalar("SELECT DISTINCT tbl FROM pg_sync_changes ORDER BY tbl")
            .fetch_all(&self.raw)
            .await
            .expect("query")
    }
}

fn sorted(mut tables: Vec<&str>) -> Vec<String> {
    tables.sort_unstable();
    tables.into_iter().map(str::to_owned).collect()
}

/// A throwaway PostgreSQL database.
struct Pg {
    admin: sqlx::PgPool,
    name: String,
    url: String,
}

impl Pg {
    async fn create() -> Option<Self> {
        let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
            return None;
        };
        let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
        let admin = sqlx::PgPool::connect(&admin_url)
            .await
            .expect("connect admin");
        sqlx::query(AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&admin)
            .await
            .expect("create");
        let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
        let url = format!("{base}/{name}");
        Some(Self { admin, name, url })
    }

    /// A migrated transfer connection.
    async fn target(&self) -> PgConnection {
        let mut conn = transfer::connect(&self.url, Duration::from_secs(5))
            .await
            .expect("connect");
        transfer::migrate(&mut conn).await.expect("migrate");
        conn
    }

    async fn drop(self) {
        sqlx::query(AssertSqlSafe(format!(
            "DROP DATABASE {} WITH (FORCE)",
            self.name
        )))
        .execute(&self.admin)
        .await
        .expect("drop");
        self.admin.close().await;
    }
}

struct Seeded {
    dir: Item,
    sub: Item,
    inner: Item,
    file: Item,
    meta: BlobId,
    history: JobId,
}

/// One row or more in every table.
async fn seed(s: &Sqlite) -> Seeded {
    let db = &s.db;
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let nzb = BlobId::new();
    let meta = BlobId::new();
    let mut dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    dir.nzb_blob = Some(nzb);
    let mut file = Item::child(&dir, "b.mkv", ItemKind::NzbFile).expect("valid");
    file.meta_blob = Some(meta);
    file.nzb_blob = Some(nzb);
    file.size = Some(5 << 32);
    let mut other = Item::child(&dir, "a.mkv", ItemKind::Multipart).expect("valid");
    other.encrypted = true;
    let sub = Item::child(&dir, "sub", ItemKind::Directory).expect("valid");
    let inner = Item::child(&sub, "c.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(
        &[dir.clone(), file.clone(), other, sub.clone(), inner.clone()],
        &[(nzb, b"nzb"), (meta, b"meta")],
    )
    .await
    .expect("insert");
    db.enqueue(
        &QueueEntry {
            id: JobId::new(),
            created_at: 1,
            file_name: "job.nzb".into(),
            job_name: "job".into(),
            category: "tv".into(),
            priority: 0,
            post_processing: 3,
            nzb_blob: BlobId::new(),
            nzb_size: 3,
            total_bytes: 5 << 32,
            paused: true,
            retry_at: Some(10),
            attempts: 2,
            stuck_count: 0,
            last_error: Some("busy".into()),
            content_hash: None,
        },
        b"queued nzb",
    )
    .await
    .expect("enqueue");
    let history = JobId::new();
    for (id, dir) in [(history, Some(dir.id)), (JobId::new(), None)] {
        db.add_history(
            &HistoryEntry {
                id,
                created_at: 2,
                file_name: "job.nzb".into(),
                job_name: "job".into(),
                category: "tv".into(),
                status: JobStatus::Completed,
                total_bytes: 10,
                download_seconds: 3,
                fail_message: None,
                download_dir: dir,
                nzb_blob: None,
                storage: Some("/mnt/job".into()),
            },
            &[],
            &[],
        )
        .await
        .expect("history");
    }
    db.record_health_check(&HealthCheck {
        created_at: 7,
        item_id: file.id,
        path: file.path.clone(),
        result: HealthResult::Missing,
        repair: Repair::ActionNeeded,
        message: Some("gone".into()),
    })
    .await
    .expect("health");
    db.request_media_probe(file.id, 5).await.expect("probe");
    db.reject_release("release", &["<a@b>", "<c@d>"], i64::MAX / 2)
        .await
        .expect("reject");
    s.exec("INSERT INTO arr_refresh (category, generation) VALUES ('movies', 1)")
        .await;
    let instance = uuid::Uuid::now_v7();
    assert!(
        db.reserve_searches(instance, "movie", &[1, 2], 10)
            .await
            .expect("reserve")
    );
    db.record_owner(instance, file.id, Some(7), "/tv/b.mkv", None)
        .await
        .expect("owner");
    db.push_arr_event(instance, b"{}").await.expect("event");
    db.write_config(&[("a", "1"), ("b", "2")], &[])
        .await
        .expect("config");
    assert!(db.create_admin("admin", "hash").await.expect("admin"));
    db.record_handoffs(&[Handoff {
        path: "/library/b.mkv".into(),
        item: file.id,
        job: None,
        size: None,
    }])
    .await
    .expect("handoff");
    db.put_conversion(&Conversion {
        source: "/library/x.mkv".into(),
        target: "/library/y.strm".into(),
        item: file.id,
        state: 1,
        from_link: true,
        to_link: false,
        original: "o".into(),
        written: "w".into(),
        owners: "[]".into(),
        reason: None,
    })
    .await
    .expect("conversion");
    s.exec(&format!(r#"INSERT INTO "completed_link_removals"("item_id","removed_at") VALUES(X'{item}',1);
INSERT INTO "import_sources"("item_id","provenance") VALUES(X'{item}','fixture');
INSERT INTO "health_reader_holds"("item_id") VALUES(X'{item}');
INSERT INTO "urgent_health"("item_id") VALUES(X'{item}');
INSERT INTO "regrab_intents"("item_id","scope","payload","revision","created_at","updated_at","terminal") VALUES(X'{item}','fixture','{{}}',1,1,1,1);
INSERT INTO "library_catalogue"("root","scope","scanned_at","requested_at","requested_generation","completed_generation","attempted_generation","attempted_at","error") VALUES('/fixture-library','fixture',NULL,1,1,1,1,1,NULL);
INSERT INTO "library_links"("root","path","relative_path","group_name","item_id","pointer_hash","link_kind","video") VALUES('/fixture-library','fixture','fixture','fixture',X'{item}','fixture',1,1);
INSERT INTO "plex_classification_snapshot"("slot","payload") VALUES(1,'{{}}');
INSERT OR REPLACE INTO "content_sweep_state"("slot","payload","revision") VALUES(1,'{{}}',1);
INSERT INTO "content_sweep_items"("item_id") VALUES(X'{item}');
INSERT INTO "repair_lineage"("slot","ledger_id","epoch") VALUES(1,X'{item}',1);
INSERT INTO "repair_observations"("item_id","revision") VALUES(X'{item}',X'7272727272727272727272727272727272727272727272727272727272727272');
INSERT OR REPLACE INTO "par2_attempt_state"("slot","revision") VALUES(1,1);
INSERT INTO "par2_attempts"("id","item_id","source","state","priority","next_attempt","updated_at","revision","payload") VALUES(X'{item}',X'{item}',X'7272727272727272727272727272727272727272727272727272727272727272','queued',1,1,1,1,'{{}}');
INSERT OR REPLACE INTO "prefetch_wire_budget"("slot","day","charged","granted") VALUES(1,1,1,1);
INSERT INTO "prefetch_history"("id","recorded_at","payload") VALUES(X'{item}',1,'{{}}');
INSERT INTO "library_scan"("root","scope","worker","device","inode","generation","visited","linked","path_bytes","started_at","updated_at") VALUES('/fixture-library','fixture','fixture','fixture','fixture',1,1,1,1,1,1);
INSERT INTO "library_scan_dirs"("root","path","depth","done") VALUES('/fixture-library','fixture',1,1);
INSERT INTO "library_scan_seen"("root","path") VALUES('/fixture-library','fixture');
INSERT INTO "library_scan_links"("root","path","relative_path","group_name","item_id","pointer_hash","link_kind","video") VALUES('/fixture-library','fixture','fixture','fixture',X'{item}','fixture',1,1);
INSERT INTO "migration_submissions"("intent","scope","payload_sha256","job_id","created_at") VALUES('fixture','fixture','fixture',X'{item}',1);
INSERT INTO "legacy_repair_holds"("evidence_id","item_id","kind","payload","request_id","observed_at","resolved_at","proof","created_at") VALUES('fixture',NULL,'fixture','{{}}',NULL,1,NULL,NULL,1);"#,item=file.id.0.simple())).await;
    Seeded {
        dir,
        sub,
        inner,
        file,
        meta,
        history,
    }
}

#[tokio::test]
async fn capture_logs_every_table() {
    let s = Sqlite::open().await;
    s.db.start_capture().await.expect("capture");
    seed(&s).await;
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "inserts");
    assert!(s.db.pending_changes().await.expect("count") > 0);

    s.exec("DELETE FROM pg_sync_changes").await;
    for table in ALL {
        let first: String =
            sqlx::query_scalar("SELECT name FROM pragma_table_info($1) ORDER BY cid LIMIT 1")
                .bind(table)
                .fetch_one(&s.raw)
                .await
                .expect("column");
        s.exec(&format!("UPDATE {table} SET {first} = {first}"))
            .await;
    }
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "updates");

    s.exec("DELETE FROM pg_sync_changes").await;
    for table in ALL.iter().rev() {
        s.exec(&format!("DELETE FROM {table}")).await;
    }
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "deletes");
    s.close().await;
}

#[tokio::test]
async fn capture_state_follows_objects() {
    let s = Sqlite::open().await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    assert_eq!(s.db.pending_changes().await.expect("count"), 0);
    s.db.start_capture().await.expect("start");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.exec(
        "CREATE TABLE config_new (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL); \
         INSERT INTO config_new SELECT key, value FROM config; \
         DROP TABLE config; ALTER TABLE config_new RENAME TO config;",
    )
    .await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Broken);
    s.db.start_capture().await.expect("restart");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.db.stop_capture().await.expect("stop");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    s.db.stop_capture().await.expect("stop is idempotent");
    s.close().await;
}

#[tokio::test]
async fn capture_ignores_other_pg_sync_objects() {
    let s = Sqlite::open().await;
    s.exec("CREATE TABLE pg_sync_notes (note TEXT); INSERT INTO pg_sync_notes VALUES ('kept')")
        .await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    s.db.start_capture().await.expect("start");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.db.stop_capture().await.expect("stop");
    let kept: String = sqlx::query_scalar("SELECT note FROM pg_sync_notes")
        .fetch_one(&s.raw)
        .await
        .expect("still there");
    assert_eq!(kept, "kept");
    s.close().await;
}

#[tokio::test]
async fn copy_needs_capture() {
    let s = Sqlite::open().await;
    let Some(pg) = Pg::create().await else {
        s.close().await;
        return;
    };
    let mut target = pg.target().await;
    assert!(matches!(
        s.db.copy_all(&mut target, |_| {}).await,
        Err(infinidysk_db::Error::NotCapturing)
    ));
    drop(target);
    pg.drop().await;
    s.close().await;
}

async fn sorted_ids(db: &Db) -> Vec<ItemId> {
    let mut ids = db.item_ids().await.expect("ids");
    ids.sort_unstable_by_key(|id| id.0);
    ids
}

#[tokio::test]
async fn copy_then_verify_matches() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    let reports = std::sync::Mutex::new(Vec::new());
    s.db.copy_all(&mut target, |p| reports.lock().expect("lock").push(p))
        .await
        .expect("copy");
    assert!(
        reports
            .lock()
            .expect("lock")
            .iter()
            .any(|p| p.table == "items" && p.done == p.total && p.total > 0)
    );
    assert_eq!(s.db.sync(&mut target, true, |_| {}).await.expect("sync"), 0);
    drop(target);
    let copy = Db::connect(&pg.url).await.expect("open target");
    assert_eq!(
        copy.config().await.expect("config"),
        s.db.config().await.expect("config")
    );
    assert_eq!(sorted_ids(&copy).await, sorted_ids(&s.db).await);
    assert_eq!(
        copy.admin().await.expect("admin"),
        s.db.admin().await.expect("admin")
    );
    copy.close().await;
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn changes_during_capture_reach_target() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    let seeded = seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    s.db.copy_all(&mut target, |_| {}).await.expect("copy");

    let added = Item::child(&seeded.dir, "d.mkv", ItemKind::NzbFile).expect("valid");
    s.db.insert(&[added], &[]).await.expect("insert");
    s.db.update_blob(seeded.meta, b"meta2").await.expect("blob");
    assert_eq!(
        s.db.delete_history(&[seeded.history])
            .await
            .expect("history"),
        1
    );
    // Rename a folder, then move its child out and delete the folder.
    let (sub, inner) = (seeded.sub.id.0, seeded.inner.id.0);
    let renamed = format!("{}2", seeded.sub.path);
    sqlx::query("UPDATE items SET name = 'sub2', path = $1 WHERE id = $2")
        .bind(&renamed)
        .bind(sub)
        .execute(&s.raw)
        .await
        .expect("rename");
    sqlx::query("UPDATE items SET path = $1 WHERE id = $2")
        .bind(format!("{renamed}/c.mkv"))
        .bind(inner)
        .execute(&s.raw)
        .await
        .expect("rename child");
    sqlx::query("UPDATE items SET parent_id = $1, path = $2 WHERE id = $3")
        .bind(seeded.dir.id.0)
        .bind(format!("{}/c.mkv", seeded.dir.path))
        .bind(inner)
        .execute(&s.raw)
        .await
        .expect("move");
    s.db.delete_item(seeded.sub.id)
        .await
        .expect("delete folder");
    // Swap two paths: only parking keeps the unique index happy.
    let mut tx = s.raw.begin().await.expect("begin");
    for (path, id) in [
        ("/swap".to_owned(), seeded.file.id.0),
        (seeded.file.path.clone(), inner),
        (format!("{}/c.mkv", seeded.dir.path), seeded.file.id.0),
    ] {
        sqlx::query("UPDATE items SET path = $1 WHERE id = $2")
            .bind(path)
            .bind(id)
            .execute(&mut *tx)
            .await
            .expect("swap");
    }
    tx.commit().await.expect("commit");
    s.db.set_admin("root", "hash2").await.expect("admin");
    s.db.write_config(&[("c", "3")], &["a"])
        .await
        .expect("config");

    let applied = s.db.sync(&mut target, true, |_| {}).await.expect("sync");
    assert!(applied > 0);
    assert_eq!(s.db.pending_changes().await.expect("count"), 0);
    assert_eq!(
        s.db.sync(&mut target, true, |_| {}).await.expect("again"),
        0
    );
    drop(target);
    let copy = Db::connect(&pg.url).await.expect("open target");
    assert_eq!(
        copy.admin().await.expect("admin"),
        s.db.admin().await.expect("admin")
    );
    assert_eq!(
        copy.item(seeded.inner.id)
            .await
            .expect("item")
            .map(|i| i.path),
        Some(seeded.file.path.clone())
    );
    assert!(copy.item(seeded.sub.id).await.expect("item").is_none());
    copy.close().await;
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn sync_detects_divergence() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    s.db.copy_all(&mut target, |_| {}).await.expect("copy");
    sqlx::query("UPDATE config SET value = 'changed' WHERE key = 'a'")
        .execute(&mut target)
        .await
        .expect("diverge");
    let error =
        s.db.sync(&mut target, true, |_| {})
            .await
            .expect_err("mismatch");
    assert!(matches!(error, infinidysk_db::Error::Mismatch("config")));
    assert!(error.needs_full_copy());
    drop(target);
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn check_target_reports_foreign_and_used_tables() {
    let Some(pg) = Pg::create().await else { return };
    let empty = transfer::check_target(&pg.url).await.expect("check");
    assert!(empty.foreign_tables.is_empty() && empty.used.is_empty());
    assert!(empty.can_create);
    assert!(!empty.version.is_empty() && !empty.collation.is_empty());

    let mut conn = pg.target().await;
    let migrated = transfer::check_target(&pg.url).await.expect("check");
    assert_eq!(migrated.used, Vec::<&str>::new());
    sqlx::query("UPDATE content_sweep_state SET payload='{}'")
        .execute(&mut conn)
        .await
        .expect("active sweep state");
    assert_eq!(
        transfer::check_target(&pg.url)
            .await
            .expect("sweep target")
            .used,
        ["content_sweep_state"]
    );
    sqlx::query("UPDATE content_sweep_state SET payload='null'")
        .execute(&mut conn)
        .await
        .expect("pristine sweep state");

    sqlx::query("UPDATE par2_attempt_state SET revision=1")
        .execute(&mut conn)
        .await
        .expect("active repair state");
    assert_eq!(
        transfer::check_target(&pg.url)
            .await
            .expect("repair target")
            .used,
        ["par2_attempt_state"]
    );
    sqlx::query("UPDATE par2_attempt_state SET revision=0")
        .execute(&mut conn)
        .await
        .expect("pristine repair state");
    sqlx::query("UPDATE prefetch_wire_budget SET charged=1,granted=1")
        .execute(&mut conn)
        .await
        .expect("active wire budget");
    assert_eq!(
        transfer::check_target(&pg.url)
            .await
            .expect("wire target")
            .used,
        ["prefetch_wire_budget"]
    );
    sqlx::query("UPDATE prefetch_wire_budget SET charged=0,granted=0")
        .execute(&mut conn)
        .await
        .expect("pristine wire budget");

    sqlx::query("INSERT INTO blobs (id, data) VALUES ($1, $2)")
        .bind(uuid::Uuid::now_v7())
        .bind(&b"x"[..])
        .execute(&mut conn)
        .await
        .expect("blob");
    assert_eq!(
        transfer::check_target(&pg.url).await.expect("check").used,
        ["blobs"]
    );
    sqlx::query("CREATE TABLE other (x int)")
        .execute(&mut conn)
        .await
        .expect("create");
    assert_eq!(
        transfer::check_target(&pg.url)
            .await
            .expect("check")
            .foreign_tables,
        ["other"]
    );
    drop(conn);
    pg.drop().await;
}
