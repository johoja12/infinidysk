use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use infinidysk_db::{
    ArrOwner, Db, Defect, Error, FileQuery, FileSort, FirstCheck, Handoff, HealthCheck,
    HealthResult, HealthState, HealthStatus, HistoryEntry, HistoryQuery, Item, JobStatus,
    MediaProbe, OwnedFile, ProbeAttempt, ProbeOutcome, QueueEntry, Recheck, Repair, SubtreeStats,
};

/// Exercises the tree, blob and config paths that must behave the same on both backends.
async fn exercise(db: &Db) {
    wire_allowance(db).await;
    warming_history(db).await;
    repair_lineage(db).await;
    repair_observations(db).await;
    repair_attempts(db).await;
    repair_completion(db).await;
    content_sweep(db).await;
    fair_rechecks(db).await;
    library_catalogue(db).await;
    library_item_pages(db).await;
    library_staging(db).await;
    db.save_plex_classification("fixture-snapshot")
        .await
        .expect("classification snapshot");
    assert_eq!(
        db.plex_classification()
            .await
            .expect("classification snapshot")
            .as_deref(),
        Some("fixture-snapshot")
    );
    assert!(
        !db.config()
            .await
            .expect("settings")
            .iter()
            .any(|(key, _)| key == "plex_classification_snapshot")
    );

    // Unreferenced NZBs go at once until the retention section at the end.
    db.set_nzb_retention_days(0);
    migration_admission(db).await;
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    assert_eq!(content.id, ItemId::CONTENT);
    assert_eq!(
        db.item(ItemId::ROOT).await.expect("query").map(|i| i.path),
        Some("/".into())
    );

    let mut dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    let nzb = BlobId::new();
    dir.nzb_blob = Some(nzb);
    let mut file = Item::child(&dir, "b.mkv", ItemKind::NzbFile).expect("valid");
    let meta = BlobId::new();
    file.meta_blob = Some(meta);
    file.nzb_blob = Some(nzb);
    file.size = Some(5 << 32);
    let mut other = Item::child(&dir, "a.mkv", ItemKind::Multipart).expect("valid");
    other.encrypted = true;
    db.insert(
        &[dir.clone(), file.clone(), other.clone()],
        &[(nzb, b"nzb"), (meta, b"meta")],
    )
    .await
    .expect("insert");

    let batch = db
        .items_by_ids(&[
            file.id.0,
            other.id.0,
            file.id.0,
            uuid::Uuid::from_u128(u128::MAX),
        ])
        .await
        .expect("catalogue join");
    assert_eq!(batch.len(), 2);
    assert!(batch.iter().any(|i| i.id == file.id && i.size == file.size));
    assert!(batch.iter().any(|i| i.id == other.id && i.encrypted));
    assert_eq!(db.items_by_ids(&[]).await.expect("empty batch").len(), 0);
    assert!(db.items_by_ids(&vec![file.id.0; 1001]).await.is_err());

    assert!(
        db.item_descends_from(file.id, dir.id)
            .await
            .expect("import subtree")
    );
    assert!(
        !db.item_descends_from(file.id, other.id)
            .await
            .expect("unrelated subtree")
    );
    let intent = db
        .prepare_regrab(file.id, "scope", "prepared")
        .await
        .expect("prepare regrab");
    assert_eq!(intent.revision, 0);
    assert_eq!(
        db.prepare_regrab(file.id, "other-scope", "wrong")
            .await
            .expect("existing regrab")
            .scope,
        "scope"
    );
    assert!(
        db.checkpoint_regrab(file.id, "scope", 0, "delete-pending", false)
            .await
            .expect("checkpoint")
    );
    assert!(
        !db.checkpoint_regrab(file.id, "scope", 0, "stale", false)
            .await
            .expect("stale checkpoint")
    );
    assert!(
        !db.checkpoint_regrab(file.id, "other-scope", 1, "wrong", false)
            .await
            .expect("wrong scope")
    );
    assert_eq!(
        db.regrab_intent(file.id)
            .await
            .expect("read intent")
            .expect("intent retained")
            .payload,
        "delete-pending"
    );
    let page = db
        .regrab_page(i64::MAX, ItemId(uuid::Uuid::nil()), 1)
        .await
        .expect("journal page");
    assert_eq!(page.len(), 1);
    assert_eq!(page[0].0, file.id);
    assert_eq!(page[0].1.revision, 1);
    assert_eq!(
        db.regrab_page(page[0].1.updated_at, page[0].0, 1)
            .await
            .expect("next journal page"),
        []
    );
    assert_eq!(
        db.regrab_intents(i64::MAX, false, 10)
            .await
            .expect("observer schedule")
            .len(),
        1
    );
    let now = infinidysk_db::now_ms();
    let held = HealthStatus {
        state: HealthState::Repairing,
        next_check: Some(now + 900_000),
        ..HealthStatus::default()
    };
    db.set_health(file.id, &held, Recheck::None)
        .await
        .expect("health persistence");
    db.hold_repair_for_reader(file.id)
        .await
        .expect("health persistence");
    assert_eq!(
        db.reader_held_repairs().await.expect("health persistence"),
        [file.id]
    );
    assert!(
        db.release_reader_repair(file.id)
            .await
            .expect("health persistence")
    );
    assert!(
        !db.release_reader_repair(file.id)
            .await
            .expect("health persistence")
    );
    let suspect = HealthStatus {
        state: HealthState::Suspect,
        next_check: Some(now + 300_000),
        ..held
    };
    db.set_health(file.id, &suspect, Recheck::None)
        .await
        .expect("health persistence");
    db.request_recheck(file.id, Recheck::Playback)
        .await
        .expect("health persistence");
    db.postpone_health(
        file.id,
        now + 300_000,
        db.health(file.id)
            .await
            .expect("health")
            .expect("item")
            .1
            .recheck_generation,
    )
    .await
    .expect("health persistence");
    assert!(
        db.health_is_urgent(file.id)
            .await
            .expect("health persistence")
    );
    assert_eq!(
        db.due_health_work(now, false, 10)
            .await
            .expect("health persistence"),
        [] as [(Item, HealthStatus); 0]
    );
    assert_eq!(
        db.due_health_work(now + 300_001, false, 10)
            .await
            .expect("health persistence")[0]
            .0
            .id,
        file.id
    );
    db.set_health(file.id, &HealthStatus::default(), Recheck::Playback)
        .await
        .expect("health persistence");
    // Only terminal outcomes clear persistent playback urgency.
    db.set_health(
        file.id,
        &HealthStatus {
            state: HealthState::Healthy,
            ..HealthStatus::default()
        },
        Recheck::None,
    )
    .await
    .expect("health persistence");
    assert!(
        !db.health_is_urgent(file.id)
            .await
            .expect("health persistence")
    );

    db.set_health(file.id, &HealthStatus::default(), Recheck::None)
        .await
        .expect("restore initial state");
    assert_eq!(file.path, "/content/job/b.mkv");
    assert_eq!(
        db.item_by_path(&file.path).await.expect("query"),
        Some(file.clone())
    );
    let names: Vec<_> = db
        .children(dir.id)
        .await
        .expect("query")
        .into_iter()
        .map(|i| i.name)
        .collect();
    assert_eq!(names, ["a.mkv", "b.mkv"]);
    let mut names = db.child_names(dir.id).await.expect("query");
    names.sort();
    assert_eq!(names, ["a.mkv", "b.mkv"]);
    let ids = |items: Vec<Item>| items.into_iter().map(|i| i.id).collect::<Vec<_>>();
    assert_eq!(
        ids(db.files_in_id_range(dir.id, None).await.expect("query")),
        [file.id, other.id],
        "directories are skipped"
    );
    assert_eq!(
        ids(db
            .files_in_id_range(file.id, Some(other.id))
            .await
            .expect("query")),
        [file.id]
    );
    assert_eq!(
        db.blob(meta).await.expect("query").as_deref(),
        Some(&b"meta"[..])
    );
    db.update_blob(meta, b"meta2").await.expect("update");
    assert_eq!(
        db.blob(meta).await.expect("query").as_deref(),
        Some(&b"meta2"[..])
    );

    let dup = Item {
        id: ItemId::new(),
        ..other.clone()
    };
    assert!(db.insert(&[dup], &[]).await.is_err(), "paths are unique");
    assert!(matches!(
        Item::child(&dir, "x/y", ItemKind::NzbFile),
        Err(Error::InvalidName(_))
    ));

    db.record_health_check(&HealthCheck {
        created_at: 7,
        item_id: file.id,
        path: file.path.clone(),
        result: HealthResult::Missing,
        repair: Repair::ActionNeeded,
        message: Some("gone".into()),
    })
    .await
    .expect("record");
    let names = |q: FileQuery<'static>| async move {
        let (rows, total) = db.files(&q).await.expect("query");
        let names: Vec<String> = rows.into_iter().map(|r| r.item.name).collect();
        (names, total)
    };
    let page = |offset| FileQuery {
        limit: 10,
        offset,
        ..FileQuery::default()
    };
    assert_eq!(
        names(page(0)).await,
        (vec!["job".into(), "a.mkv".into(), "b.mkv".into()], 3),
        "directories first"
    );
    assert_eq!(
        names(FileQuery {
            limit: 1,
            ..page(1)
        })
        .await
        .0,
        ["a.mkv"]
    );
    let flat = FileQuery {
        files_only: true,
        sort: FileSort::Size,
        descending: true,
        ..page(0)
    };
    assert_eq!(names(flat.clone()).await.0, ["b.mkv", "a.mkv"]);
    for (q, want) in [
        (
            FileQuery {
                search: Some("B.M"),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                kind: Some(ItemKind::Multipart),
                ..flat.clone()
            },
            "a.mkv",
        ),
        (
            FileQuery {
                min_size: Some(1),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                result: Some(HealthResult::Missing),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                state: Some(HealthState::Unchecked),
                min_size: Some(1),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                repair: Some(Repair::ActionNeeded),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                encrypted: Some(true),
                ..flat.clone()
            },
            "a.mkv",
        ),
        (
            FileQuery {
                encrypted: Some(false),
                ..flat.clone()
            },
            "b.mkv",
        ),
    ] {
        assert_eq!(names(q.clone()).await.0, [want], "{q:?}");
    }
    assert_eq!(
        names(FileQuery {
            search: Some("%"),
            ..flat.clone()
        })
        .await
        .1,
        0
    );
    assert_eq!(
        names(FileQuery {
            category: Some("job"),
            ..flat.clone()
        })
        .await
        .1,
        2
    );
    assert_eq!(
        names(FileQuery {
            category: Some("jo"),
            ..flat.clone()
        })
        .await
        .1,
        0
    );
    let (rows, _) = db
        .files(&FileQuery {
            search: Some("b.mkv"),
            ..flat
        })
        .await
        .expect("query");
    assert_eq!(
        rows[0].health.as_ref().map(|h| h.repair),
        Some(Repair::ActionNeeded)
    );
    assert_eq!(db.item_checks(file.id, 5).await.expect("query").len(), 1);

    // Health work: unchecked first, recheck reasons survive a concurrent write, hidden
    // items leave listings.
    let due = |routine| async move {
        let work = db.due_health_work(100, routine, 10).await.expect("query");
        work.into_iter().map(|(i, _)| i.id).collect::<Vec<_>>()
    };
    assert_eq!(due(true).await, [file.id], "only files with metadata");
    assert_eq!(due(false).await, [] as [ItemId; 0], "routine checks off");
    db.request_recheck(file.id, Recheck::Playback)
        .await
        .expect("recheck");
    db.request_recheck(file.id, Recheck::User)
        .await
        .expect("recheck");
    let (_, status) = db.health(file.id).await.expect("query").expect("exists");
    assert_eq!(status.recheck, Recheck::Playback, "the urgent reason wins");
    assert_eq!(due(false).await, [file.id], "rechecks run with routine off");
    let hidden = HealthStatus {
        state: HealthState::Hidden,
        next_check: Some(1_000),
        hidden_until: Some(2_000),
        ..status
    };
    db.set_health(file.id, &hidden, Recheck::User)
        .await
        .expect("set");
    let (item, stored) = db.health(file.id).await.expect("query").expect("exists");
    assert_eq!(item.health_state, HealthState::Hidden);
    assert_eq!(stored.recheck, Recheck::Playback, "not the handled reason");
    assert_eq!(
        due(true).await,
        [] as [ItemId; 0],
        "hidden waits for its time"
    );
    assert_eq!(
        names(flat.clone()).await.0,
        ["a.mkv"],
        "hidden files are not listed"
    );
    assert_eq!(
        names(FileQuery {
            state: Some(HealthState::Hidden),
            ..flat.clone()
        })
        .await
        .0,
        ["b.mkv"]
    );
    assert_eq!(
        db.health_summary().await.expect("query"),
        [(HealthState::Hidden, 1)]
    );

    db.reject_release("rel", &["a@b", "c@d"], 50)
        .await
        .expect("reject");
    media_probes(db, &other, &file).await;
    assert!(db.is_rejected("rel", &[], 10).await.expect("query"));
    assert!(db.is_rejected("other", &["c@d"], 10).await.expect("query"));
    let listed = db.rejected_releases(10).await.expect("query");
    assert_eq!(
        listed
            .iter()
            .map(|r| (r.name.as_str(), r.expires_at))
            .collect::<Vec<_>>(),
        [("rel", 50)]
    );
    assert!(listed[0].rejected_at > 0, "stamped from the id");
    assert_eq!(db.rejected_releases(50).await.expect("query"), []);
    assert!(
        !db.is_rejected("rel", &["a@b"], 50).await.expect("query"),
        "expired"
    );
    db.prune_rejected_releases(50).await.expect("prune");
    assert!(!db.is_rejected("other", &["c@d"], 0).await.expect("query"));
    assert_eq!(
        db.subtree_stats(dir.id).await.expect("query"),
        SubtreeStats {
            files: 2,
            directories: 1,
            bytes: 5 << 32
        }
    );

    db.delete_item(dir.id).await.expect("delete");
    assert!(
        db.regrab_intent(file.id)
            .await
            .expect("detached intent")
            .is_some(),
        "repair observations survive source removal"
    );
    assert_eq!(
        db.item(file.id).await.expect("query"),
        None,
        "subtree cascades"
    );
    assert_eq!(
        db.blob(meta).await.expect("query"),
        None,
        "unused blobs go too"
    );
    assert_eq!(db.blob(nzb).await.expect("query"), None);
    assert_eq!(
        db.media_probe(other.id).await.expect("query"),
        MediaProbe::default(),
        "probe rows cascade"
    );

    db.write_config(&[("sab", "{}"), ("providers", "[1]")], &[])
        .await
        .expect("write");
    db.write_config(&[("providers", "[2]")], &["sab"])
        .await
        .expect("write");
    assert_eq!(
        db.config().await.expect("query"),
        [("providers".into(), "[2]".into())]
    );

    let job = QueueEntry {
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
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert_eq!(
        db.unhashed_jobs().await.expect("query"),
        [(job.id, job.nzb_blob)]
    );
    db.set_content_hash(job.id, "ab").await.expect("hash");
    assert_eq!(db.unhashed_jobs().await.expect("query"), []);
    assert_eq!(
        db.job_with_content("ab").await.expect("query"),
        Some(job.id)
    );
    assert_eq!(db.job_with_content("cd").await.expect("query"), None);
    let job = QueueEntry {
        content_hash: Some("ab".into()),
        ..job
    };
    assert_eq!(db.next_jobs(10, 1).await.expect("query"), [], "paused");
    assert_eq!(db.pause_jobs(&[job.id], false).await.expect("resume"), 1);
    assert_eq!(db.next_jobs(9, 1).await.expect("query"), [], "waiting");
    db.retry_job(job.id, 10, "down").await.expect("retry");
    let job = QueueEntry {
        paused: false,
        attempts: 1,
        last_error: Some("down".into()),
        ..job
    };
    assert_eq!(
        db.next_jobs(10, 1).await.expect("query"),
        std::slice::from_ref(&job)
    );
    let mut out = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    out.nzb_blob = Some(job.nzb_blob);
    let history = HistoryEntry {
        id: job.id,
        created_at: 2,
        file_name: job.file_name.clone(),
        job_name: job.job_name.clone(),
        category: job.category.clone(),
        status: JobStatus::Completed,
        total_bytes: job.total_bytes,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(out.id),
        nzb_blob: Some(job.nzb_blob),
        storage: None,
    };
    let handoff = Handoff {
        path: "/strm/tv/job/a.strm".into(),
        item: out.id,
        job: Some(job.id),
        size: None,
    };
    assert!(
        db.finish_job(
            &history,
            &[out.clone()],
            &[],
            &[],
            std::slice::from_ref(&handoff)
        )
        .await
        .expect("finish")
    );
    assert!(
        !db.finish_job(&history, &[], &[], &[], &[])
            .await
            .expect("finish"),
        "a job no longer queued is not finished twice"
    );
    assert_eq!(
        db.handoff_item(&handoff.path).await.expect("query"),
        Some(out.id)
    );
    let gone = [
        Handoff {
            path: "/strm/tv/gone/a.strm".into(),
            job: Some(JobId::new()),
            ..handoff.clone()
        },
        Handoff {
            path: "/strm/tv/old/a.srt".into(),
            item: ItemId::new(),
            job: None,
            size: Some(7),
        },
    ];
    db.record_handoffs(&gone).await.expect("record");
    let mut stale = db.stale_handoffs().await.expect("query");
    stale.sort_by(|a, b| a.path.cmp(&b.path));
    assert_eq!(
        stale,
        [gone[0].clone(), gone[1].clone()],
        "job or item gone"
    );
    db.forget_handoffs(&[gone[0].path.clone(), gone[1].path.clone()])
        .await
        .expect("forget");
    assert_eq!(db.stale_handoffs().await.expect("query"), []);
    let category = job.category.to_lowercase();
    let pending = db.pending_refreshes().await.expect("query");
    assert_eq!(pending, [(category.clone(), 1)]);
    let gone = JobId::new();
    db.add_history(
        &HistoryEntry {
            id: gone,
            download_dir: None,
            ..history.clone()
        },
        &[],
        &[],
    )
    .await
    .expect("history");
    db.refreshed(&category, 1).await.expect("refreshed");
    assert_eq!(
        db.pending_refreshes().await.expect("query"),
        [(category.clone(), 2)],
        "a finish after the read stays pending"
    );
    db.refreshed(&category, 2).await.expect("refreshed");
    assert_eq!(db.pending_refreshes().await.expect("query"), []);

    // Search limit: per media, all or nothing for a pack, sliding window, per instance.
    let arr = uuid::Uuid::now_v7();
    let reserve = |ids: &'static [i64], at: i64| db.reserve_searches(arr, "episode", ids, at);
    for at in [0, 1, 2] {
        assert!(reserve(&[1], at).await.expect("reserve"));
    }
    assert!(
        !reserve(&[2, 1], 3).await.expect("reserve"),
        "episode 1 is at the limit"
    );
    assert!(
        reserve(&[2], 3).await.expect("reserve"),
        "the denied pack reserved nothing"
    );
    assert!(
        db.reserve_searches(uuid::Uuid::now_v7(), "episode", &[1], 3)
            .await
            .expect("reserve")
    );
    assert!(
        reserve(&[1], infinidysk_db::SEARCH_WINDOW_MS)
            .await
            .expect("reserve")
    );
    assert_eq!(db.queue().await.expect("query"), []);
    assert_eq!(
        db.history_entry(job.id).await.expect("query"),
        Some(history.clone())
    );
    assert_eq!(
        db.completed_dirs(ItemId::CONTENT).await.expect("query"),
        [out.clone()]
    );
    assert!(db.is_completed_dir(out.id).await.expect("query"));
    assert!(!db.is_completed_dir(content.id).await.expect("query"));

    // History filters run in SQL; a completed job without its folder reports as failed.
    let mut both = vec![job.id, gone];
    both.sort_by_key(|id| id.0);
    let all = HistoryQuery::default();
    assert_eq!(history_ids(db, all).await, (both.clone(), 2));
    let completed = HistoryQuery {
        status: Some(JobStatus::Completed),
        ..all
    };
    assert_eq!(history_ids(db, completed).await, (vec![job.id], 1));
    let failed = HistoryQuery {
        status: Some(JobStatus::Failed),
        search: Some("JOB.N"),
        category: Some("tv"),
        ..all
    };
    assert_eq!(history_ids(db, failed).await, (vec![gone], 1));
    // The cutoff drops stored failures only; a missing folder cannot be retried.
    let stored_failure = JobId::new();
    db.add_history(
        &HistoryEntry {
            id: stored_failure,
            status: JobStatus::Failed,
            download_dir: None,
            ..history.clone()
        },
        &[],
        &[],
    )
    .await
    .expect("history");
    let recent = HistoryQuery {
        failed_since: Some(i64::MAX),
        ..all
    };
    assert_eq!(history_ids(db, recent).await, (both.clone(), 2));
    let recent_ids = HistoryQuery {
        ids: Some(&[stored_failure, gone]),
        ..recent
    };
    assert_eq!(history_ids(db, recent_ids).await, (vec![gone], 1));
    let recent_failed = HistoryQuery {
        status: Some(JobStatus::Failed),
        ..recent
    };
    assert_eq!(history_ids(db, recent_failed).await, (vec![gone], 1));
    let recent_failed_ids = HistoryQuery {
        ids: Some(&[stored_failure, gone]),
        ..recent_failed
    };
    assert_eq!(history_ids(db, recent_failed_ids).await, (vec![gone], 1));
    let old = HistoryQuery {
        failed_since: Some(i64::MIN),
        ..all
    };
    assert_eq!(history_ids(db, old).await.1, 3);
    let at_cutoff = HistoryQuery {
        failed_since: Some(history.created_at),
        ..all
    };
    let (rows, total) = history_ids(db, at_cutoff).await;
    assert_eq!(total, 3);
    assert!(rows.contains(&stored_failure));
    let at_cutoff_ids = HistoryQuery {
        ids: Some(&[stored_failure]),
        ..at_cutoff
    };
    assert_eq!(
        history_ids(db, at_cutoff_ids).await,
        (vec![stored_failure], 1)
    );
    assert_eq!(
        db.delete_history(&[stored_failure]).await.expect("delete"),
        1
    );
    let by_id = HistoryQuery {
        ids: Some(&[gone, JobId::new(), job.id]),
        offset: 1,
        limit: Some(5),
        ..all
    };
    assert_eq!(history_ids(db, by_id).await, (vec![both[1]], 2), "paged");
    let escaped = HistoryQuery {
        search: Some("j%"),
        ..all
    };
    let other = HistoryQuery {
        category: Some("TV"),
        ..all
    };
    for q in [escaped, other] {
        assert_eq!(history_ids(db, q).await, (vec![], 0));
    }
    assert_eq!(db.history_ids(true).await.expect("query"), []);
    assert_eq!(db.history_ids(false).await.expect("query").len(), 2);

    assert_eq!(db.delete_history(&[job.id]).await.expect("delete"), 1);
    assert_eq!(
        db.job_at(&out.path).await.expect("query"),
        Some((job.id, job.job_name.clone())),
        "provenance outlives history"
    );
    assert_eq!(db.job_at(&content.path).await.expect("query"), None);

    // Arr owners: an item is free once its last owner forgets it.
    assert_eq!(
        db.job_dir(job.id).await.expect("query"),
        Some(out.path.clone())
    );
    let sub = Item::child(&out, "S", ItemKind::Directory).expect("valid");
    let ep = Item::child(&sub, "e.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(&[sub.clone(), ep.clone()], &[])
        .await
        .expect("insert");
    let (a, b) = (uuid::Uuid::now_v7(), uuid::Uuid::now_v7());
    db.record_owner(a, ep.id, Some(7), "/tv/S/e.mkv", Some(job.id))
        .await
        .expect("record");
    db.record_owner(b, ep.id, None, "/tv2/S/e.mkv", None)
        .await
        .expect("record");
    db.rename_owner(a, &OwnedFile::Id(7), "/tv/S/f.mkv")
        .await
        .expect("rename");
    let forget = |arr, which| async move { db.forget_owners(arr, &which).await.expect("forget") };
    assert_eq!(forget(a, OwnedFile::Path("/tv/S/e.mkv".into())).await, []);
    let mut owners = db.owner_instances(ep.id).await.expect("query");
    owners.sort_unstable();
    assert_eq!(owners, [a.min(b), a.max(b)], "renamed, not forgotten");
    assert_eq!(
        forget(a, OwnedFile::Under("/tv/S".into())).await,
        [],
        "b still owns it"
    );
    assert_eq!(forget(b, OwnedFile::Under("/tv".into())).await, []);
    assert_eq!(forget(b, OwnedFile::Under("/tv2/".into())).await, [ep.id]);
    db.record_owner(a, ep.id, Some(9), "/tv/x.mkv", None)
        .await
        .expect("record");
    let owner = ArrOwner {
        item: ep.id,
        file_id: Some(9),
        path: "/tv/x.mkv".into(),
    };
    assert_eq!(
        db.owners(a).await.expect("query"),
        std::slice::from_ref(&owner)
    );
    let stale = ArrOwner {
        path: "/tv/old.mkv".into(),
        ..owner.clone()
    };
    assert_eq!(
        db.forget_owner(a, &stale).await.expect("forget"),
        None,
        "changed since read"
    );
    db.refile_owner(a, &owner, 10).await.expect("refile");
    let owner = ArrOwner {
        file_id: Some(10),
        ..owner
    };
    assert_eq!(
        db.owners(a).await.expect("query"),
        std::slice::from_ref(&owner)
    );
    assert_eq!(
        db.forget_owner(a, &owner).await.expect("forget"),
        Some(ep.id)
    );

    // Cleanup deletes an item only while no Arr owns it.
    let re = Item::child(&sub, "f.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(std::slice::from_ref(&re), &[])
        .await
        .expect("insert");
    let owner = ArrOwner {
        item: re.id,
        file_id: Some(11),
        path: "/tv/f.mkv".into(),
    };
    db.record_owner(a, re.id, owner.file_id, &owner.path, None)
        .await
        .expect("record");
    assert_eq!(
        db.forget_owner(a, &owner).await.expect("forget"),
        Some(re.id)
    );
    let owner = ArrOwner {
        item: re.id,
        file_id: None,
        path: "/tv2/f.mkv".into(),
    };
    db.record_owner(b, re.id, owner.file_id, &owner.path, None)
        .await
        .expect("record");
    assert!(
        !db.delete_unowned(re.id).await.expect("delete"),
        "b adopted it"
    );
    assert!(db.item(re.id).await.expect("query").is_some());
    assert_eq!(db.owner_instances(re.id).await.expect("query"), [b]);
    assert_eq!(
        db.forget_owner(b, &owner).await.expect("forget"),
        Some(re.id)
    );
    assert!(db.delete_unowned(re.id).await.expect("delete"));
    assert_eq!(db.item(re.id).await.expect("query"), None);
    assert!(
        !db.delete_unowned(ItemId::new()).await.expect("delete"),
        "no such item"
    );

    db.push_arr_event(a, b"1").await.expect("push");
    db.push_arr_event(b, b"2").await.expect("push");
    let (id, arr, body) = db.next_arr_event().await.expect("query").expect("stored");
    assert_eq!((arr, body.as_slice()), (a, &b"1"[..]));
    db.drop_arr_event(id).await.expect("drop");
    let (id, _, _) = db.next_arr_event().await.expect("query").expect("stored");
    db.drop_arr_event(id).await.expect("drop");
    assert_eq!(db.next_arr_event().await.expect("query"), None);

    let job = QueueEntry {
        id: JobId::new(),
        nzb_blob: BlobId::new(),
        ..job
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert!(db.remove_job(job.id).await.expect("remove"));
    assert!(!db.remove_job(job.id).await.expect("remove"));
    assert_eq!(db.blob(job.nzb_blob).await.expect("query"), None);

    // NZB retention: kept through the window, reused by identical content, then expired.
    db.set_nzb_retention_days(1);
    let job = QueueEntry {
        id: JobId::new(),
        nzb_blob: BlobId::new(),
        content_hash: Some("ef".into()),
        ..job
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert_eq!(
        db.nzbs_with_content("ef").await.expect("query"),
        [job.nzb_blob]
    );
    assert!(db.remove_job(job.id).await.expect("remove"));
    assert_eq!(db.delete_expired_nzbs().await.expect("expire"), 0);
    let again = QueueEntry {
        id: JobId::new(),
        ..job.clone()
    };
    db.enqueue(&again, b"unused").await.expect("reuse");
    assert_eq!(
        db.blob(job.nzb_blob).await.expect("query").as_deref(),
        Some(&b"nzb"[..]),
        "the kept NZB is reused"
    );
    db.set_nzb_retention_days(0);
    assert_eq!(
        db.delete_expired_nzbs().await.expect("expire"),
        0,
        "referenced again"
    );
    db.set_nzb_retention_days(1);
    assert!(db.remove_job(again.id).await.expect("remove"));
    db.set_nzb_retention_days(0);
    assert_eq!(db.delete_expired_nzbs().await.expect("expire"), 1);
    assert_eq!(db.blob(job.nzb_blob).await.expect("query"), None);

    assert_eq!(db.prune_history(3).await.expect("prune"), 1);
    assert_eq!(db.prune_history(3).await.expect("prune"), 0);
    assert!(
        db.item(out.id).await.expect("query").is_some(),
        "pruning keeps downloads"
    );
    db.delete_item(ep.id).await.expect("delete");
    assert_eq!(
        db.item(out.id).await.expect("query"),
        None,
        "an emptied job folder goes"
    );
    assert!(db.item(content.id).await.expect("query").is_some());
    unlinked(db).await;
    restored_nzb_links(db).await;
    first_checks(db).await;
    mapped_source_fences(db).await;
}

/// A finished job stores its files' first health checks with its output.
async fn first_checks(db: &Db) {
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("query")
        .expect("seeded");
    let job = QueueEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "first.nzb".into(),
        job_name: "first".into(),
        category: "tv".into(),
        priority: 0,
        post_processing: 3,
        nzb_blob: BlobId::new(),
        nzb_size: 3,
        total_bytes: 1,
        paused: false,
        retry_at: None,
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    let dir = Item::child(&content, "first", ItemKind::Directory).expect("valid");
    let good = Item::child(&dir, "a.mkv", ItemKind::NzbFile).expect("valid");
    let broken = Item::child(&dir, "b.mp4", ItemKind::NzbFile).expect("valid");
    let healthy = FirstCheck {
        status: HealthStatus {
            state: HealthState::Healthy,
            last_check: Some(5),
            next_check: Some(6),
            ..HealthStatus::default()
        },
        result: HealthResult::Healthy,
        message: None,
        defect: None,
        analyze: true,
    };
    let defect = Defect {
        offset: 32,
        size: 1 << 40,
        seen_at: 5,
        source: 3,
    };
    let suspect = FirstCheck {
        status: HealthStatus {
            state: HealthState::Suspect,
            next_check: Some(7),
            ..healthy.status
        },
        result: HealthResult::Damaged,
        message: Some("broken".into()),
        defect: Some(defect),
        analyze: false,
    };
    let history = HistoryEntry {
        id: job.id,
        created_at: 2,
        file_name: job.file_name.clone(),
        job_name: job.job_name.clone(),
        category: job.category.clone(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(dir.id),
        nzb_blob: Some(job.nzb_blob),
        storage: None,
    };
    let checks = [(good.id, healthy.clone()), (broken.id, suspect.clone())];
    let items = [dir, good.clone(), broken.clone()];
    assert!(
        db.finish_job(&history, &items, &[], &checks, &[])
            .await
            .expect("finish")
    );
    for (item, check) in [(&good, &healthy), (&broken, &suspect)] {
        let (_, status) = db.health(item.id).await.expect("query").expect("stored");
        assert_eq!(status, check.status);
        let recorded = HealthCheck {
            created_at: 5,
            item_id: item.id,
            path: item.path.clone(),
            result: check.result,
            repair: Repair::None,
            message: check.message.clone(),
        };
        assert_eq!(db.item_checks(item.id, 5).await.expect("query"), [recorded]);
    }
    let probe = db.media_probe(good.id).await.expect("query");
    assert_eq!(
        (probe.requested_at, probe.structural_hold),
        (Some(5), false)
    );
    let probe = db.media_probe(broken.id).await.expect("query");
    assert_eq!(
        (probe.requested_at, probe.defect, probe.structural_hold),
        (None, Some(defect), true)
    );
}

async fn history_ids(db: &Db, q: HistoryQuery<'_>) -> (Vec<JobId>, u64) {
    let (rows, total) = db.history_page(&q).await.expect("history");
    (rows.into_iter().map(|(h, _)| h.id).collect(), total)
}

/// Requests coalesce, hidden files wait, completion keeps newer requests and metadata.
async fn media_probes(db: &Db, open: &Item, hidden: &Item) {
    assert!(db.request_media_probe(hidden.id, 5).await.expect("request"));
    assert!(db.request_media_probe(open.id, 10).await.expect("request"));
    assert!(
        db.request_media_probe(open.id, 20).await.expect("request"),
        "a coalesced request is accepted"
    );
    let (item, at) = db
        .next_media_probe()
        .await
        .expect("query")
        .expect("pending");
    assert_eq!((item.id, at), (open.id, 10), "hidden files wait");
    let found = ProbeAttempt {
        at: 30,
        outcome: ProbeOutcome::Discovered,
        reason: None,
        info: Some("{\"v\":1}"),
    };
    db.finish_media_probe(open.id, 9, &found)
        .await
        .expect("finish");
    assert_eq!(
        db.media_probe(open.id).await.expect("query").requested_at,
        Some(10),
        "a newer request survives"
    );
    db.finish_media_probe(open.id, 10, &found)
        .await
        .expect("finish");
    let failed = ProbeAttempt {
        at: 40,
        outcome: ProbeOutcome::Inconclusive,
        reason: Some("timed out"),
        info: None,
    };
    db.finish_media_probe(open.id, 10, &failed)
        .await
        .expect("finish");
    let defect = Defect {
        offset: 1 << 33,
        size: 4,
        seen_at: 50,
        source: -7,
    };
    db.set_structural(open.id, Some(&defect), true)
        .await
        .expect("set");
    db.set_structural(ItemId::new(), Some(&defect), true)
        .await
        .expect("gone items are skipped");
    let dir = db
        .item(open.parent.expect("has a parent"))
        .await
        .expect("query")
        .expect("exists");
    let gone = Item::child(&dir, "gone.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(std::slice::from_ref(&gone), &[])
        .await
        .expect("insert");
    db.delete_item(gone.id).await.expect("delete");
    assert!(
        !db.request_media_probe(gone.id, 1)
            .await
            .expect("a deleted item is skipped")
    );
    assert_eq!(
        db.media_probe(gone.id).await.expect("query"),
        MediaProbe::default()
    );
    assert!(
        !db.request_media_probe(ItemId::new(), 1)
            .await
            .expect("query"),
        "unknown items are skipped"
    );
    assert_eq!(
        db.media_probe(open.id).await.expect("query"),
        MediaProbe {
            requested_at: None,
            attempted_at: Some(40),
            outcome: Some(ProbeOutcome::Inconclusive),
            reason: Some("timed out".into()),
            info: Some("{\"v\":1}".into()),
            info_at: Some(30),
            defect: Some(defect),
            structural_hold: true,
        },
        "a failed attempt keeps the last metadata"
    );
    assert_eq!(
        db.media_probe(hidden.id).await.expect("query").requested_at,
        Some(5)
    );
}

/// Unlinked-file cleanup: only files outside every download in history go, then the
/// folders that leaves empty.
async fn unlinked(db: &Db) {
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let cat = Item::child(&content, "maint", ItemKind::Directory).expect("valid");
    let mut kept = Item::child(&cat, "kept", ItemKind::Directory).expect("valid");
    let mut loose = Item::child(&cat, "loose", ItemKind::Directory).expect("valid");
    let (kept_nzb, loose_nzb) = (BlobId::new(), BlobId::new());
    kept.nzb_blob = Some(kept_nzb);
    loose.nzb_blob = Some(loose_nzb);
    let kept_file = Item::child(&kept, "a.mkv", ItemKind::NzbFile).expect("valid");
    let loose_file = Item::child(&loose, "b.mkv", ItemKind::NzbFile).expect("valid");
    let mid = Item::child(&loose, "mid", ItemKind::Directory).expect("valid");
    let empty = Item::child(&mid, "sub", ItemKind::Directory).expect("valid");
    db.insert(
        &[
            cat.clone(),
            kept.clone(),
            loose.clone(),
            kept_file.clone(),
            loose_file.clone(),
            mid,
            empty.clone(),
        ],
        &[(kept_nzb, b"nzb"), (loose_nzb, b"nzb")],
    )
    .await
    .expect("insert");
    db.import_history(&[HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "kept.nzb".into(),
        job_name: "kept".into(),
        category: "maint".into(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(kept.id),
        nzb_blob: Some(kept_nzb),
        storage: None,
    }])
    .await
    .expect("history");
    let before = infinidysk_db::now_ms() + 1;
    let paths: Vec<String> = db
        .files_outside_history(before)
        .await
        .expect("query")
        .into_iter()
        .map(|i| i.path)
        .filter(|p| p.starts_with("/content/maint/"))
        .collect();
    assert_eq!(paths, std::slice::from_ref(&loose_file.path));
    assert!(
        !db.delete_unlinked(kept_file.id, before)
            .await
            .expect("delete")
    );
    assert!(
        !db.delete_unlinked(loose_file.id, 0).await.expect("delete"),
        "files added after the run started stay"
    );
    assert!(
        db.delete_unlinked(loose_file.id, before)
            .await
            .expect("delete")
    );
    assert_eq!(
        db.delete_empty_dirs(before).await.expect("sweep"),
        3,
        "sub, mid and the job folder"
    );
    assert_eq!(db.delete_empty_dirs(before).await.expect("sweep"), 0);
    assert_eq!(db.item(loose.id).await.expect("query"), None);
    assert!(db.item(kept.id).await.expect("query").is_some());
    assert!(db.item(cat.id).await.expect("query").is_some());
    assert_eq!(db.blob(loose_nzb).await.expect("query"), None);

    let folders: Vec<_> = (0..501)
        .map(|index| {
            let mut folder =
                Item::child(&cat, &format!("empty-{index}"), ItemKind::Directory).expect("valid");
            folder.created_at = 1;
            folder
        })
        .collect();
    db.insert(&folders, &[]).await.expect("insert");
    assert_eq!(
        db.delete_empty_dirs(before).await.expect("first batch"),
        500
    );
    assert_eq!(db.children(cat.id).await.expect("remaining").len(), 2);
    assert_eq!(db.delete_empty_dirs(before).await.expect("second batch"), 1);
    assert_eq!(db.delete_empty_dirs(before).await.expect("finished"), 0);
}

async fn restored_nzb_links(db: &Db) {
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("NZB recovery fixture")
        .expect("NZB recovery fixture");
    let item = Item::child(&content, "restored", ItemKind::NzbFile).expect("NZB recovery fixture");
    let download = BlobId::new();
    db.insert(std::slice::from_ref(&item), &[(download, b"nzb")])
        .await
        .expect("NZB recovery fixture");
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "restored.nzb".into(),
        job_name: "restored".into(),
        category: String::new(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: None,
        nzb_blob: None,
        storage: None,
    };
    db.import_history(std::slice::from_ref(&history))
        .await
        .expect("NZB recovery fixture");
    let (items, histories) = db.missing_nzb_links().await.expect("NZB recovery fixture");
    assert!(items.contains(&item.id));
    assert!(histories.contains(&history.id));
    let missing = BlobId::new();
    assert_eq!(
        db.restore_nzb_links(&[(item.id, missing)], &[(history.id, missing)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    assert_eq!(
        db.restore_nzb_links(&[(item.id, download)], &[(history.id, download)])
            .await
            .expect("NZB recovery fixture"),
        2
    );
    let other = BlobId::new();
    db.insert(&[], &[(other, b"other")])
        .await
        .expect("NZB recovery fixture");
    assert_eq!(
        db.restore_nzb_links(&[(item.id, other)], &[(history.id, other)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    assert_eq!(
        db.item(item.id)
            .await
            .expect("NZB recovery fixture")
            .expect("NZB recovery fixture")
            .nzb_blob,
        Some(download)
    );
    assert_eq!(
        db.history_entry(history.id)
            .await
            .expect("NZB recovery fixture")
            .expect("NZB recovery fixture")
            .nzb_blob,
        Some(download)
    );
    assert_eq!(
        db.restore_nzb_links(&[(ItemId::new(), download)], &[(JobId::new(), download)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    let (items, histories) = db.missing_nzb_links().await.expect("NZB recovery fixture");
    assert!(!items.contains(&item.id));
    assert!(!histories.contains(&history.id));
}

/// An owner insert still in progress when cleanup checks the item keeps the item.
async fn adoption_during_cleanup_keeps_the_item(db: &Db, url: &str) {
    const RECORD: &str = "INSERT INTO arr_owners (instance_id, item_id, file_id, path, job_id) \
                          VALUES ($1, $2, $3, $4, $5) ON CONFLICT (instance_id, item_id) DO UPDATE \
                          SET file_id = excluded.file_id, path = excluded.path, job_id = excluded.job_id";
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let item = Item::child(&content, "adopted.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(std::slice::from_ref(&item), &[])
        .await
        .expect("insert");
    let arr = uuid::Uuid::now_v7();
    let id = item.id;
    let cleanup = |db: Db| tokio::spawn(async move { db.delete_unowned(id).await });
    let pause = std::time::Duration::from_millis(200);
    let deleted = if url.starts_with("sqlite:") {
        let raw = sqlx::SqlitePool::connect(url).await.expect("open");
        let mut tx = raw.begin().await.expect("begin");
        sqlx::query(RECORD)
            .bind(arr)
            .bind(id.0)
            .bind(Some(1_i64))
            .bind("/tv/adopted.mkv")
            .bind(None::<uuid::Uuid>)
            .execute(&mut *tx)
            .await
            .expect("record");
        let deleted = cleanup(db.clone());
        tokio::time::sleep(pause).await;
        tx.commit().await.expect("commit");
        let deleted = deleted.await.expect("join");
        raw.close().await;
        deleted
    } else {
        let raw = sqlx::PgPool::connect(url).await.expect("open");
        let mut tx = raw.begin().await.expect("begin");
        sqlx::query(RECORD)
            .bind(arr)
            .bind(id.0)
            .bind(Some(1_i64))
            .bind("/tv/adopted.mkv")
            .bind(None::<uuid::Uuid>)
            .execute(&mut *tx)
            .await
            .expect("record");
        let deleted = cleanup(db.clone());
        let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(10);
        while sqlx::query_scalar::<_, i64>(
            "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() \
             AND wait_event_type = 'Lock' AND query LIKE '%FROM items WHERE id = $1 FOR UPDATE'",
        )
        .fetch_one(&raw)
        .await
        .expect("query")
            == 0
        {
            assert!(
                tokio::time::Instant::now() < deadline,
                "cleanup never waited on the item lock"
            );
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        tx.commit().await.expect("commit");
        let deleted = deleted.await.expect("join");
        raw.close().await;
        deleted
    };
    assert!(
        !matches!(deleted, Ok(true)),
        "cleanup deleted an adopted item: {deleted:?}"
    );
    assert!(db.item(id).await.expect("query").is_some());
    assert_eq!(db.owner_instances(id).await.expect("query"), [arr]);
}

#[tokio::test]
async fn sqlite_storage_round_trips() {
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    exercise(&db).await;
    legacy_source_retirement(&db).await;
    legacy_repair_holds(&db).await;
    completed_pointer_removal(&db).await;
    adoption_during_cleanup_keeps_the_item(&db, &url).await;
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn pausing_writes_waits_for_permits_and_holds_new_ones() {
    use std::time::Duration;
    use tokio::time::timeout;
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let wait = Duration::from_millis(50);
    let permit = db.write_permit().await;
    let pause = db.pause_writes();
    tokio::pin!(pause);
    assert!(
        timeout(wait, &mut pause).await.is_err(),
        "a permit holds the pause back"
    );
    drop(permit);
    let paused = timeout(wait, pause)
        .await
        .expect("pauses once the permit drops");
    let clone = db.clone();
    assert!(
        timeout(wait, clone.write_permit()).await.is_err(),
        "the pause holds new permits back, on clones too"
    );
    drop(paused);
    timeout(wait, db.write_permit())
        .await
        .expect("permits resume");
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn a_database_from_another_build_is_refused() {
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    Db::connect(&url).await.expect("connect").close().await;
    let pool = sqlx::SqlitePool::connect(&url).await.expect("open");
    sqlx::query(
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         VALUES (999, 'unknown', TRUE, X'00', 0)",
    )
    .execute(&pool)
    .await
    .expect("insert");
    pool.close().await;
    assert!(matches!(
        Db::connect(&url).await,
        Err(infinidysk_db::Error::OtherSchema(_))
    ));
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

/// Runs when `INFINIDYSK_TEST_POSTGRES_URL` points at a server whose user may create databases.
#[tokio::test]
async fn postgres_storage_round_trips() {
    let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
        eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
        return;
    };
    let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
    let admin = sqlx::PgPool::connect(&admin_url)
        .await
        .expect("connect admin");
    let create = sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}"));
    sqlx::query(create).execute(&admin).await.expect("create");
    let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");

    let url = format!("{base}/{name}");
    let db = Db::connect(&url).await.expect("connect");
    exercise(&db).await;
    legacy_source_retirement(&db).await;
    legacy_repair_holds(&db).await;
    completed_pointer_removal(&db).await;
    adoption_during_cleanup_keeps_the_item(&db, &url).await;
    db.close().await;
    let drop = sqlx::AssertSqlSafe(format!("DROP DATABASE {name}"));
    sqlx::query(drop).execute(&admin).await.expect("drop");
}

async fn completed_pointer_removal(db: &Db) {
    let root = db
        .item(ItemId::CONTENT)
        .await
        .expect("completed-pointer fixture")
        .expect("completed-pointer fixture");
    let dir = Item::child(&root, "completed-pointer-fixture", ItemKind::Directory)
        .expect("completed-pointer fixture");
    let file = Item::child(&dir, "file.mkv", ItemKind::NzbFile).expect("completed-pointer fixture");
    db.insert(&[dir.clone(), file.clone()], &[])
        .await
        .expect("completed-pointer fixture");
    assert!(
        !db.completed_link_removed(file.id)
            .await
            .expect("completed-pointer fixture")
    );
    db.remove_completed_links(file.id)
        .await
        .expect("completed-pointer fixture");
    db.remove_completed_links(file.id)
        .await
        .expect("completed-pointer fixture");
    assert!(
        db.completed_link_removed(file.id)
            .await
            .expect("completed-pointer fixture")
    );
    assert_eq!(
        db.completed_link_children(dir.id)
            .await
            .expect("completed-pointer fixture"),
        Vec::<Item>::new()
    );
    assert_eq!(
        db.item(file.id).await.expect("completed-pointer fixture"),
        Some(file.clone())
    );
    db.remove_completed_links(dir.id)
        .await
        .expect("completed-pointer fixture");
    assert!(
        db.completed_link_removed(dir.id)
            .await
            .expect("completed-pointer fixture")
    );
    assert_eq!(
        db.item(file.id).await.expect("completed-pointer fixture"),
        Some(file)
    );
}

#[tokio::test]
async fn completed_pointer_removal_survives_a_database_restart() {
    let path = std::env::temp_dir().join(format!("id-completed-removal-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.unwrap();
    completed_pointer_removal(&db).await;
    let file = db
        .item_by_path("/content/completed-pointer-fixture/file.mkv")
        .await
        .unwrap()
        .unwrap();
    db.close().await;
    let reopened = Db::connect(&url).await.unwrap();
    assert!(reopened.completed_link_removed(file.id).await.unwrap());
    assert!(reopened.item(file.id).await.unwrap().is_some());
    reopened.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn fork_import_provenance_and_reader_holds_survive_restart_and_cascade() {
    let path = std::env::temp_dir().join(format!("id-fork-provenance-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("database");
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("root")
        .expect("root exists");
    let item = Item::child(&content, "fixture.dat", ItemKind::NzbFile).expect("item");
    let status = HealthStatus {
        state: HealthState::Repairing,
        next_check: Some(infinidysk_db::now_ms() + 900_000),
        ..HealthStatus::default()
    };
    db.import_items_with_sources(
        &[(item.clone(), status)],
        &[],
        &[(item.id, "source-receipt".into())],
    )
    .await
    .expect("atomic import");
    db.hold_repair_for_reader(item.id).await.expect("hold");
    db.request_recheck(item.id, Recheck::Playback)
        .await
        .expect("urgency");
    drop(db);
    let db = Db::connect(&url).await.expect("reopen");
    assert_eq!(
        db.import_source(item.id)
            .await
            .expect("provenance")
            .as_deref(),
        Some("source-receipt")
    );
    assert_eq!(db.reader_held_repairs().await.expect("holds"), [item.id]);
    assert!(
        db.health_is_urgent(item.id)
            .await
            .expect("urgency survives")
    );
    assert!(db.release_reader_repair(item.id).await.expect("release"));
    db.delete_item(item.id).await.expect("delete");
    assert_eq!(db.import_source(item.id).await.expect("cascade"), None);
    assert_eq!(
        db.reader_held_repairs().await.expect("cascade"),
        [] as [ItemId; 0]
    );
    assert!(!db.health_is_urgent(item.id).await.expect("cascade"));
    drop(db);
    let _ = std::fs::remove_file(path);
}

/// Shared SQLite/PostgreSQL proof: imports cannot starve explicit work, and completion
/// or exception backoff cannot consume a same-priority request admitted during work.
async fn fair_rechecks(db: &Db) {
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("content")
        .expect("exists");
    let dir = Item::child(&content, "fair-health", ItemKind::Directory).expect("directory");
    let mut requested = Item::child(&dir, "requested.bin", ItemKind::NzbFile).expect("file");
    let mut imported = Item::child(&dir, "imported.bin", ItemKind::NzbFile).expect("file");
    let meta = BlobId::new();
    requested.meta_blob = Some(meta);
    let imported_meta = BlobId::new();
    imported.meta_blob = Some(imported_meta);
    db.insert(
        &[dir.clone(), requested.clone(), imported.clone()],
        &[(meta, b"meta"), (imported_meta, b"meta")],
    )
    .await
    .expect("insert");
    db.request_recheck(requested.id, Recheck::User)
        .await
        .expect("request");
    let (_, captured) = db
        .health(requested.id)
        .await
        .expect("health")
        .expect("exists");
    let first = db
        .due_health_work(infinidysk_db::now_ms(), true, 1)
        .await
        .expect("queue");
    assert_eq!(
        first[0].0.id, requested.id,
        "explicit requests precede unchecked imports"
    );
    db.request_recheck(requested.id, Recheck::User)
        .await
        .expect("concurrent request");
    let healthy = HealthStatus {
        state: HealthState::Healthy,
        next_check: Some(i64::MAX),
        ..captured
    };
    db.set_health(requested.id, &healthy, captured.recheck)
        .await
        .expect("complete");
    let (_, pending) = db
        .health(requested.id)
        .await
        .expect("health")
        .expect("exists");
    assert_eq!(
        pending.recheck,
        Recheck::User,
        "new request survives completion"
    );
    assert!(pending.recheck_generation > captured.recheck_generation);
    db.postpone_health(requested.id, i64::MAX, captured.recheck_generation)
        .await
        .expect("stale failure");
    assert_eq!(
        db.health(requested.id)
            .await
            .expect("health")
            .expect("exists")
            .1
            .recheck,
        Recheck::User,
        "stale failure cannot clear new work"
    );
    let (_, pending) = db
        .health(requested.id)
        .await
        .expect("health")
        .expect("exists");
    db.set_health(requested.id, &pending, pending.recheck)
        .await
        .expect("complete newer request");
    assert_eq!(
        db.health(requested.id)
            .await
            .expect("health")
            .expect("exists")
            .1
            .recheck,
        Recheck::None
    );
    assert_eq!(
        db.due_health_work(infinidysk_db::now_ms(), true, 1)
            .await
            .expect("queue")[0]
            .0
            .id,
        imported.id,
        "completed work yields to imports"
    );
    db.delete_item(dir.id).await.expect("cleanup");
}

async fn library_catalogue(db: &Db) {
    use infinidysk_db::catalogue::Link;
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("content")
        .expect("exists");
    let mut item = Item::child(&content, "catalogue-fixture.mkv", ItemKind::NzbFile).expect("file");
    item.size = Some(900);
    db.insert(std::slice::from_ref(&item), &[])
        .await
        .expect("insert");
    let root = "/catalogue-fixture";
    let link = Link {
        root: root.into(),
        path: format!("{root}/Collection/file.mkv"),
        relative_path: "Collection/file.mkv".into(),
        group_name: "Collection".into(),
        item_id: item.id.0,
        pointer_hash: "fingerprint".into(),
        link_kind: 1,
        video: 1,
    };
    db.catalogue_replace(root, "scope-a", std::slice::from_ref(&link), 100, 0)
        .await
        .expect("snapshot");
    let read = || {
        db.catalogue_links(
            Some(root),
            Some("Collection"),
            Some(item.id.0),
            true,
            "",
            10,
        )
    };
    assert_eq!(
        read().await.expect("links").as_slice(),
        std::slice::from_ref(&link)
    );
    let grouped = db.catalogue_groups(root, "", 10).await.expect("groups");
    assert_eq!(
        (grouped[0].files, grouped[0].bytes, grouped[0].unavailable),
        (1, 900, 0)
    );
    db.catalogue_failed(root, "scope-b", "storage unavailable", 0)
        .await
        .expect("failure");
    let roots = db.catalogue_roots().await.expect("roots");
    let saved = roots.iter().find(|r| r.root == root).expect("root");
    assert_eq!(
        saved.scope, "scope-a",
        "failed scope never replaces authority"
    );
    assert_eq!(saved.scanned_at, Some(100));
    assert_eq!(
        read().await.expect("retained").as_slice(),
        std::slice::from_ref(&link)
    );
    assert!(
        db.catalogue_replace(root, "scope-b", &[link.clone(), link.clone()], 200, 0)
            .await
            .is_err(),
        "duplicate row rejects the entire transaction"
    );
    assert_eq!(
        read().await.expect("rolled back").as_slice(),
        std::slice::from_ref(&link)
    );
    db.catalogue_request(root, "scope-a", 300)
        .await
        .expect("refresh request");
    db.catalogue_replace(root, "scope-a", std::slice::from_ref(&link), 300, 0)
        .await
        .expect("snapshot");
    assert!(
        db.catalogue_roots()
            .await
            .expect("roots")
            .iter()
            .find(|r| r.root == root)
            .expect("root")
            .requested_generation
            > 0,
        "concurrent request survives publication"
    );
    db.catalogue_request(root, "scope-a", 300)
        .await
        .expect("same millisecond request");
    db.catalogue_replace(root, "scope-a", std::slice::from_ref(&link), 300, 1)
        .await
        .expect("complete earlier request");
    let saved = db
        .catalogue_roots()
        .await
        .expect("roots")
        .into_iter()
        .find(|r| r.root == root)
        .expect("root");
    assert_eq!(
        (saved.requested_generation, saved.completed_generation),
        (2, 1),
        "same-time later request survives"
    );
    db.delete_item(item.id).await.expect("source removal");
    assert_eq!(
        db.catalogue_groups(root, "", 10)
            .await
            .expect("dangling group")[0]
            .unavailable,
        1,
        "index does not pretend source removal is a completed scan"
    );
    db.catalogue_replace(root, "scope-a", &[], 400, 2)
        .await
        .expect("complete empty scan");
    assert_eq!(read().await.expect("removed link"), [] as [Link; 0]);
}

#[tokio::test]
async fn library_index_survives_database_restart() {
    use infinidysk_db::catalogue::Link;
    let path = std::env::temp_dir().join(format!("infinidysk-catalogue-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("open");
    let link = Link {
        root: "/library".into(),
        path: "/library/Collection/file.mkv".into(),
        relative_path: "Collection/file.mkv".into(),
        group_name: "Collection".into(),
        item_id: ItemId::new().0,
        pointer_hash: "fingerprint".into(),
        link_kind: 1,
        video: 1,
    };
    db.catalogue_replace("/library", "scope", std::slice::from_ref(&link), 100, 0)
        .await
        .expect("publish");
    db.save_plex_classification("persistent-classification")
        .await
        .expect("classification");
    db.close().await;
    drop(db);
    let db = Db::connect(&url).await.expect("reopen");
    assert_eq!(
        db.plex_classification()
            .await
            .expect("restored classification")
            .as_deref(),
        Some("persistent-classification")
    );
    assert_eq!(
        db.catalogue_links(Some("/library"), None, None, true, "", 10)
            .await
            .expect("index"),
        [link]
    );
    db.close().await;
    drop(db);
    let _ = std::fs::remove_file(path);
}

async fn content_sweep(db: &Db) {
    let root = db
        .item(ItemId::CONTENT)
        .await
        .expect("query")
        .expect("root");
    let dir = Item::child(&root, "sweep%", ItemKind::Directory).expect("directory");
    let mut first = Item::child(&dir, "a.mkv", ItemKind::NzbFile).expect("file");
    let a = BlobId::new();
    first.meta_blob = Some(a);
    let mut second = Item::child(&dir, "b.mkv", ItemKind::NzbFile).expect("file");
    let b = BlobId::new();
    second.meta_blob = Some(b);
    db.insert(
        &[dir.clone(), first.clone(), second.clone()],
        &[(a, b"old"), (b, b"other")],
    )
    .await
    .expect("insert");
    assert!(
        db.update_metadata_if_current(first.id, a, b"old", b"new")
            .await
            .expect("CAS")
    );
    assert!(
        !db.update_metadata_if_current(first.id, a, b"old", b"stale")
            .await
            .expect("stale CAS")
    );
    assert!(
        !db.update_metadata_if_current(second.id, a, b"new", b"wrong-item")
            .await
            .expect("scope")
    );
    assert!(
        db.metadata_is_current(first.id, a, b"new")
            .await
            .expect("current")
    );
    assert!(
        !db.metadata_is_current(first.id, a, b"old")
            .await
            .expect("old")
    );
    let original = db.content_sweep().await.expect("state");
    assert!(
        db.start_content_sweep(original.revision, "snapshot", Some("sweep%"))
            .await
            .expect("start")
    );
    let view = db.content_sweep().await.expect("state");
    assert_eq!(view.total, 2);
    assert_eq!(view.payload, "snapshot");
    assert!(
        !db.start_content_sweep(original.revision, "stale", None)
            .await
            .expect("stale start")
    );
    let initial = db
        .next_sweep_item(None)
        .await
        .expect("cursor")
        .expect("first");
    db.delete_item(initial).await.expect("delete");
    assert_eq!(
        db.content_sweep().await.expect("snapshot persists").total,
        2
    );
    assert!(
        db.next_sweep_item(Some(initial))
            .await
            .expect("cursor")
            .is_some()
    );
    assert!(
        db.checkpoint_content_sweep(view.revision, "paused")
            .await
            .expect("checkpoint")
    );
    assert!(
        !db.checkpoint_content_sweep(view.revision, "stale")
            .await
            .expect("stale")
    );
    db.delete_item(dir.id).await.expect("cleanup");
}

async fn repair_lineage(db: &Db) {
    use infinidysk_db::repair::Lineage;
    let id = uuid::Uuid::new_v4();
    assert_eq!(db.repair_lineage().await.expect("unbound"), None);
    assert!(
        !db.bind_repair_lineage(uuid::Uuid::nil(), 0)
            .await
            .expect("nil rejected")
    );
    assert!(
        db.bind_repair_lineage(id, 0)
            .await
            .expect("initial binding")
    );
    assert!(
        db.bind_repair_lineage(id, 3)
            .await
            .expect("forward witness")
    );
    assert!(
        db.bind_repair_lineage(id, 3)
            .await
            .expect("idempotent readback")
    );
    assert!(
        !db.bind_repair_lineage(id, 2)
            .await
            .expect("late older write rejected")
    );
    assert!(
        !db.bind_repair_lineage(uuid::Uuid::new_v4(), 4)
            .await
            .expect("different ledger rejected")
    );
    assert!(
        !db.bind_repair_lineage(id, u64::MAX)
            .await
            .expect("overflow rejected")
    );
    assert_eq!(
        db.repair_lineage().await.expect("retained"),
        Some(Lineage { id, epoch: 3 })
    );
}

async fn repair_observations(db: &Db) {
    let parent = db
        .item(ItemId::CONTENT)
        .await
        .expect("root")
        .expect("content");
    let mut item =
        Item::child(&parent, "repair-observation-fixture.bin", ItemKind::NzbFile).expect("file");
    let blob = BlobId::new();
    item.meta_blob = Some(blob);
    db.insert(std::slice::from_ref(&item), &[(blob, b"current")])
        .await
        .expect("insert");
    assert!(
        db.blob_limited(blob, 6)
            .await
            .expect("bounded blob")
            .is_none()
    );
    assert_eq!(
        db.blob_limited(blob, 7)
            .await
            .expect("exact byte allowance"),
        Some(b"current".to_vec())
    );
    assert!(
        db.blob_limited(BlobId::new(), 7)
            .await
            .expect("missing blob")
            .is_none()
    );
    let generation = db
        .health(item.id)
        .await
        .expect("health")
        .expect("file")
        .1
        .recheck_generation;
    let revision = [1; 32];
    assert!(
        !db.health_repair_observed(item.id, &revision)
            .await
            .expect("absent")
    );
    assert!(
        !db.record_health_repair_observation(item.id, &revision, blob, b"stale", generation)
            .await
            .expect("stale bytes")
    );
    assert!(
        !db.record_health_repair_observation(item.id, &revision, blob, b"current", generation + 1)
            .await
            .expect("stale request")
    );
    assert!(
        !db.record_health_repair_observation(
            item.id,
            &revision,
            BlobId::new(),
            b"current",
            generation
        )
        .await
        .expect("other source")
    );
    assert!(
        db.record_health_repair_observation(item.id, &revision, blob, b"current", generation)
            .await
            .expect("current receipt")
    );
    assert!(
        db.health_repair_observed(item.id, &revision)
            .await
            .expect("persisted")
    );
    assert!(
        !db.health_repair_observed(item.id, &[2; 32])
            .await
            .expect("new revision")
    );
    db.delete_item(item.id).await.expect("delete");
    assert!(
        !db.health_repair_observed(item.id, &revision)
            .await
            .expect("cascade")
    );
}

async fn repair_attempts(db: &Db) {
    let item = ItemId::new();
    let source = [7; 32];
    let next = ItemId::new();
    let now = 10;
    let (first, concurrent) = tokio::join!(
        db.enqueue_repair_attempt(item, &source, 2, now, "{\"version\":1}"),
        db.enqueue_repair_attempt(item, &source, 2, now, "{\"version\":1}")
    );
    let first = first.expect("admission").expect("capacity");
    assert_eq!(
        first.id,
        concurrent
            .expect("concurrent admission")
            .expect("existing intent")
            .id
    );
    let duplicate = db
        .enqueue_repair_attempt(item, &source, 0, now, "replacement")
        .await
        .expect("read back accepted intent")
        .expect("existing");
    assert_eq!(first.id, duplicate.id);
    assert_eq!(
        duplicate.payload, first.payload,
        "uncertain admission must not overwrite accepted intent"
    );
    assert!(
        db.update_repair_attempt(&first, "running", 2, now, now, "running")
            .await
            .expect("claim")
    );
    assert!(
        !db.update_repair_attempt(&first, "repaired", 0, now, now, "stale")
            .await
            .expect("stale CAS")
    );
    let publication = db
        .enqueue_repair_attempt(next, &source, 2, now, "verified candidates")
        .await
        .expect("admit other")
        .expect("capacity");
    assert!(
        db.update_repair_attempt(
            &publication,
            "publishing",
            2,
            now,
            now,
            "accepted-publication-intent"
        )
        .await
        .expect("intent before mutation")
    );
    assert!(
        db.enqueue_repair_attempt(ItemId::new(), &source, 0, 0, "urgent queued work")
            .await
            .expect("urgent admission")
            .is_some()
    );
    assert_eq!(
        db.recover_running_repair_attempts(now + 1)
            .await
            .expect("restart recovery"),
        1
    );
    let deferred = db
        .repair_attempt(item, &source)
        .await
        .expect("readback")
        .expect("retained");
    assert_eq!(deferred.state, "deferred");
    let publishing = db
        .repair_attempt(next, &source)
        .await
        .expect("readback")
        .expect("retained");
    assert_eq!(publishing.state, "publishing");
    assert_eq!(
        publishing.payload, "accepted-publication-intent",
        "restart recovery cannot reset accepted/uncertain publication into a new provider attempt"
    );
    assert_eq!(
        db.next_repair_attempt(now + 1)
            .await
            .expect("priority")
            .expect("due")
            .id,
        publication.id
    );
    assert_eq!(
        db.next_repair_attempt_fair(now + 1, true)
            .await
            .expect("publication before fairness")
            .expect("due")
            .id,
        publication.id
    );
    assert!(
        db.update_repair_attempt(
            &publishing,
            "publishing",
            2,
            now + 1000,
            now,
            &publishing.payload
        )
        .await
        .expect("delay readback")
    );
    assert_eq!(
        db.next_repair_attempt_fair(now + 1, false)
            .await
            .expect("urgent ordering")
            .expect("due")
            .priority,
        0
    );
    assert_eq!(
        db.next_repair_attempt_fair(now + 1, true)
            .await
            .expect("routine turn")
            .expect("due")
            .priority,
        2
    );
    assert!(
        db.update_repair_attempt(&publishing, "invented-state", 0, now, now, "invalid")
            .await
            .is_err()
    );
    let mut wrong = deferred.clone();
    wrong.source = [9; 32].to_vec();
    assert!(
        !db.update_repair_attempt(&wrong, "running", 2, now, now, "wrong source")
            .await
            .expect("source scope CAS")
    );
    for _ in 0..124 {
        assert!(
            db.enqueue_repair_attempt(ItemId::new(), &source, 2, now, "queued")
                .await
                .expect("capacity fill")
                .is_some()
        );
    }
    let (one, two) = tokio::join!(
        db.enqueue_repair_attempt(ItemId::new(), &source, 2, now, "last capacity one"),
        db.enqueue_repair_attempt(ItemId::new(), &source, 2, now, "last capacity two")
    );
    assert_eq!(
        usize::from(one.expect("admission one").is_some())
            + usize::from(two.expect("admission two").is_some()),
        1,
        "capacity check and insertion must serialize on both backends"
    );
    assert!(
        db.enqueue_repair_attempt(ItemId::new(), &source, 2, now, "overflow")
            .await
            .expect("bounded pending")
            .is_none()
    );
    assert_eq!(
        db.enqueue_repair_attempt(item, &source, 0, now, "retry")
            .await
            .expect("accepted intent still readable")
            .expect("existing")
            .id,
        first.id
    );
    assert!(
        db.update_repair_attempt(&deferred, "superseded", 2, now, now, "source replaced")
            .await
            .expect("finished")
    );
    assert!(
        db.enqueue_repair_attempt(ItemId::new(), &source, 2, now, "new capacity")
            .await
            .expect("released capacity")
            .is_some()
    );
    assert_eq!(
        db.repair_attempts(1024)
            .await
            .expect("bounded history")
            .len(),
        129
    );
    assert!(db.repair_attempts(0).await.expect("empty page").is_empty());
    let rows = db.repair_attempts(1024).await.expect("cleanup rows");
    for row in rows {
        if matches!(row.state.as_str(), "queued" | "publishing" | "deferred") {
            assert!(
                db.update_repair_attempt(
                    &row,
                    "superseded",
                    row.priority,
                    now,
                    now,
                    "fixture completed"
                )
                .await
                .expect("cleanup pending")
            );
        }
    }
}

async fn repair_completion(db: &Db) {
    use infinidysk_db::attempts::Completion;
    let parent = db
        .item(ItemId::CONTENT)
        .await
        .expect("root")
        .expect("content");
    let mut item =
        Item::child(&parent, "repair-completion-fixture.bin", ItemKind::NzbFile).expect("item");
    let blob = BlobId::new();
    item.meta_blob = Some(blob);
    db.insert(std::slice::from_ref(&item), &[(blob, b"current")])
        .await
        .expect("insert");
    let (_, mut status) = db.health(item.id).await.expect("health").expect("file");
    status.state = HealthState::Repairing;
    db.set_health(item.id, &status, Recheck::None)
        .await
        .expect("repairing");
    let source = [8; 32];
    let row = db
        .enqueue_repair_attempt(item.id, &source, 0, 10, "verified")
        .await
        .expect("queue")
        .expect("capacity");
    assert!(
        db.update_repair_attempt(&row, "publishing", 0, 10, 10, "manifest")
            .await
            .expect("intent")
    );
    let publishing = db
        .repair_attempt(item.id, &source)
        .await
        .expect("readback")
        .expect("intent");
    assert!(
        !db.complete_repair_attempt(
            &publishing,
            Completion {
                state: "repaired",
                next_attempt: 20,
                now: 11,
                payload: "complete",
                blob,
                raw: b"stale"
            }
        )
        .await
        .expect("stale source")
    );
    assert_eq!(
        db.repair_attempt(item.id, &source)
            .await
            .expect("job")
            .expect("job")
            .revision,
        publishing.revision
    );
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Repairing
    );
    assert!(
        db.complete_repair_attempt(
            &publishing,
            Completion {
                state: "repaired",
                next_attempt: 20,
                now: 11,
                payload: "complete",
                blob,
                raw: b"current"
            }
        )
        .await
        .expect("atomic complete")
    );
    let (_, checked) = db.health(item.id).await.expect("health").expect("file");
    assert_eq!(checked.state, HealthState::Unchecked);
    assert_eq!(checked.recheck, Recheck::Playback);
    assert_eq!(checked.recheck_generation, status.recheck_generation + 1);
    assert!(db.health_is_urgent(item.id).await.expect("urgent"));
    assert!(
        !db.complete_repair_attempt(
            &publishing,
            Completion {
                state: "repaired",
                next_attempt: 20,
                now: 11,
                payload: "replay",
                blob,
                raw: b"current"
            }
        )
        .await
        .expect("accepted completion cannot replay")
    );
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .recheck_generation,
        checked.recheck_generation
    );
    let other = [9; 32];
    let row = db
        .enqueue_repair_attempt(item.id, &other, 0, 12, "verified")
        .await
        .expect("queue")
        .expect("capacity");
    assert!(
        db.update_repair_attempt(&row, "publishing", 0, 12, 12, "manifest")
            .await
            .expect("intent")
    );
    let row = db
        .repair_attempt(item.id, &other)
        .await
        .expect("readback")
        .expect("intent");
    let mut hidden = checked;
    hidden.state = HealthState::Hidden;
    db.set_health(item.id, &hidden, Recheck::None)
        .await
        .expect("replacement accepted");
    assert!(
        !db.complete_repair_attempt(
            &row,
            Completion {
                state: "healthy",
                next_attempt: 20,
                now: 13,
                payload: "complete",
                blob,
                raw: b"current"
            }
        )
        .await
        .expect("cannot reset replacement reconciliation")
    );
    assert_eq!(
        db.health(item.id)
            .await
            .expect("health")
            .expect("file")
            .1
            .state,
        HealthState::Hidden
    );
    assert!(
        db.update_repair_attempt(&row, "superseded", 0, 13, 13, "source removed")
            .await
            .expect("cleanup")
    );
    db.delete_item(item.id).await.expect("remove source");
    assert_eq!(
        db.repair_attempt(item.id, &source)
            .await
            .expect("history")
            .expect("durable history")
            .state,
        "repaired"
    );
}

async fn wire_allowance(db: &Db) {
    assert!(db.reserve_warming_wire(10, 0, 100).await.is_err());
    let (a, b) = tokio::join!(
        db.reserve_warming_wire(10, 80, 100),
        db.reserve_warming_wire(10, 80, 100)
    );
    let mut granted = [
        a.expect("first").expect("credit").granted,
        b.expect("second").expect("credit").granted,
    ];
    granted.sort_unstable();
    assert_eq!(granted, [20, 80]);
    assert!(
        db.reserve_warming_wire(10, 1, 100)
            .await
            .expect("exhausted")
            .is_none()
    );
    assert!(
        db.reserve_warming_wire(9, 1, 1000)
            .await
            .expect("rollback cannot reset")
            .is_none()
    );
    assert!(
        db.reserve_warming_wire(10, 1, 50)
            .await
            .expect("limit reduction")
            .is_none()
    );
    let next = db
        .reserve_warming_wire(11, 30, 100)
        .await
        .expect("new day")
        .expect("credit");
    assert_eq!((next.day, next.charged, next.granted), (11, 30, 30));
    let last = db
        .reserve_warming_wire(11, 100, 100)
        .await
        .expect("remaining")
        .expect("credit");
    assert_eq!(last.granted, 70);
    assert_eq!(
        db.warming_wire_usage()
            .await
            .expect("durable total")
            .charged,
        100
    );
}

async fn warming_history(db: &Db) {
    let id = uuid::Uuid::new_v4();
    db.save_prefetch("first", &[(id, 10, "outcome".into())])
        .await
        .expect("atomic warming state");
    db.save_prefetch("next", &[(id, 10, "outcome".into())])
        .await
        .expect("idempotent outcome");
    assert_eq!(
        db.prefetch_history(100).await.expect("history"),
        ["outcome"]
    );
    assert!(
        db.save_prefetch("not accepted", &[(uuid::Uuid::nil(), 11, "invalid".into())])
            .await
            .is_err()
    );
    assert_eq!(
        db.config()
            .await
            .expect("config")
            .into_iter()
            .find(|(key, _)| key == "fork_prefetch_v1")
            .expect("state")
            .1,
        "next"
    );
    db.write_config(&[], &["fork_prefetch_v1"])
        .await
        .expect("test cleanup");
}

async fn library_staging(db: &Db) {
    use infinidysk_db::catalogue::Link;
    use infinidysk_db::library_scan::{Batch, Directory};
    let root = "/staging-fixture";
    let old = Link {
        root: root.into(),
        path: format!("{root}/Collection/old-file.mkv"),
        relative_path: "Collection/old-file.mkv".into(),
        group_name: "Collection".into(),
        item_id: ItemId::new().0,
        pointer_hash: "old-pointer".into(),
        link_kind: 1,
        video: 1,
    };
    let next = Link {
        path: format!("{root}/Collection/new-file.mkv"),
        relative_path: "Collection/new-file.mkv".into(),
        pointer_hash: "new-pointer".into(),
        ..old.clone()
    };
    db.catalogue_replace(root, "old-scope", std::slice::from_ref(&old), 1, 0)
        .await
        .expect("old snapshot");
    let worker = uuid::Uuid::new_v4();
    let run = db
        .catalogue_scan_claim(root, "new-scope", worker, ("1", "2"), 0)
        .await
        .expect("claim");
    let collection = format!("{root}/Collection");
    db.catalogue_scan_checkpoint(
        root,
        &run.worker,
        &Batch {
            directory: root.into(),
            seen: vec![collection.clone()],
            directories: vec![Directory {
                path: collection.clone(),
                depth: 1,
            }],
            links: vec![],
            complete: true,
        },
    )
    .await
    .expect("root checkpoint");
    let batch = Batch {
        directory: collection.clone(),
        seen: vec![next.path.clone()],
        directories: vec![],
        links: vec![next.clone()],
        complete: false,
    };
    db.catalogue_scan_checkpoint(root, &run.worker, &batch)
        .await
        .expect("private link checkpoint");
    assert_eq!(
        db.catalogue_links(Some(root), None, None, false, "", 10)
            .await
            .expect("old snapshot"),
        vec![old.clone()]
    );
    assert!(
        !db.catalogue_scan_publish(root, "new-scope", &run.worker, 2)
            .await
            .expect("incomplete cannot publish")
    );
    let resumed = db
        .catalogue_scan_claim(root, "new-scope", uuid::Uuid::new_v4(), ("1", "2"), 0)
        .await
        .expect("resume");
    assert_eq!((resumed.visited, resumed.linked), (2, 1));
    assert!(
        db.catalogue_scan_suspend(root, &resumed.worker, false)
            .await
            .expect("foreground yield")
    );
    assert!(
        db.catalogue_scan_checkpoint(root, &resumed.worker, &batch)
            .await
            .is_err()
    );
    let resumed = db
        .catalogue_scan_claim(root, "new-scope", uuid::Uuid::new_v4(), ("1", "2"), 0)
        .await
        .expect("resume after yield");
    assert_eq!((resumed.visited, resumed.linked), (2, 1));
    assert!(
        db.catalogue_scan_checkpoint(root, &run.worker, &batch)
            .await
            .is_err(),
        "late filesystem work is fenced"
    );
    assert!(
        !db.catalogue_scan_publish(root, "new-scope", &run.worker, 2)
            .await
            .expect("old publisher is fenced")
    );
    db.catalogue_scan_checkpoint(
        root,
        &resumed.worker,
        &Batch {
            directory: collection,
            seen: vec![],
            directories: vec![],
            links: vec![],
            complete: true,
        },
    )
    .await
    .expect("directory EOF");
    db.catalogue_request(root, "new-scope", 3)
        .await
        .expect("refresh while scanning");
    assert!(
        db.catalogue_scan_publish(root, "new-scope", &resumed.worker, 4)
            .await
            .expect("atomic publication")
    );
    assert_eq!(
        db.catalogue_links(Some(root), None, None, false, "", 10)
            .await
            .expect("new snapshot"),
        vec![next]
    );
    let roots = db.catalogue_roots().await.expect("roots");
    let saved = roots.iter().find(|r| r.root == root).expect("root");
    assert_eq!(saved.completed_generation, 0);
    assert_eq!(saved.requested_generation, 1, "refresh remains pending");
    let another = db
        .catalogue_scan_claim(root, "new-scope", uuid::Uuid::new_v4(), ("1", "2"), 1)
        .await
        .expect("fresh generation");
    assert_eq!(another.visited, 0);
    db.catalogue_scan_fail(root, &run.worker)
        .await
        .expect("stale failure is ignored");
    assert!(
        db.catalogue_scan_directory(root, &another.worker)
            .await
            .expect("pending root")
            .is_some()
    );
    db.catalogue_scan_fail(root, &another.worker)
        .await
        .expect("current failure");
    assert_eq!(
        db.catalogue_links(Some(root), None, None, false, "", 10)
            .await
            .expect("failure preserves snapshot")
            .len(),
        1
    );
}

#[tokio::test]
async fn staged_library_progress_survives_database_restart_and_source_identity_changes_reset_it() {
    use infinidysk_db::library_scan::Batch;
    let path = std::env::temp_dir().join(format!("infinidysk-stage-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("open");
    let run = db
        .catalogue_scan_claim(
            "/library-stage",
            "scope",
            uuid::Uuid::new_v4(),
            ("1", "2"),
            0,
        )
        .await
        .expect("claim");
    db.catalogue_scan_checkpoint(
        "/library-stage",
        &run.worker,
        &Batch {
            directory: "/library-stage".into(),
            seen: vec!["/library-stage/file.txt".into()],
            directories: vec![],
            links: vec![],
            complete: false,
        },
    )
    .await
    .expect("checkpoint");
    db.close().await;
    drop(db);
    let db = Db::connect(&url).await.expect("reopen");
    let resumed = db
        .catalogue_scan_claim(
            "/library-stage",
            "scope",
            uuid::Uuid::new_v4(),
            ("1", "2"),
            0,
        )
        .await
        .expect("resume");
    assert_eq!(resumed.visited, 1);
    assert_eq!(
        db.catalogue_scan_seen(
            "/library-stage",
            &resumed.worker,
            &["/library-stage/file.txt".into()]
        )
        .await
        .expect("seen")
        .len(),
        1
    );
    let changed = db
        .catalogue_scan_claim(
            "/library-stage",
            "scope",
            uuid::Uuid::new_v4(),
            ("1", "3"),
            0,
        )
        .await
        .expect("changed physical root");
    assert_eq!(changed.visited, 0);
    assert_eq!(
        db.catalogue_scan_seen(
            "/library-stage",
            &changed.worker,
            &["/library-stage/file.txt".into()]
        )
        .await
        .expect("reset seen")
        .len(),
        0
    );
    db.catalogue_retain(&[])
        .await
        .expect("removed roots forget only derived metadata");
    assert_eq!(db.catalogue_scans().await.expect("stages").len(), 0);
    assert_eq!(db.catalogue_roots().await.expect("roots").len(), 0);
    db.close().await;
    drop(db);
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

async fn migration_admission(db: &Db) {
    let job = QueueEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "fixture.nzb".into(),
        job_name: "fixture".into(),
        category: "migration-fixture".into(),
        priority: 0,
        post_processing: 0,
        nzb_blob: BlobId::new(),
        nzb_size: 7,
        total_bytes: 7,
        paused: false,
        retry_at: None,
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    let receipt = infinidysk_db::MigrationSubmission {
        intent: "fixture-intent".into(),
        scope: "fixture-options".into(),
        payload_sha256: "fixture-digest".into(),
        job_id: job.id.0,
        created_at: 1,
    };
    assert_eq!(
        db.enqueue_migration(&job, b"fixture", &receipt)
            .await
            .expect("atomic admission"),
        receipt
    );
    let replay = QueueEntry {
        id: JobId::new(),
        nzb_blob: BlobId::new(),
        ..job.clone()
    };
    let candidate = infinidysk_db::MigrationSubmission {
        job_id: replay.id.0,
        ..receipt.clone()
    };
    assert_eq!(
        db.enqueue_migration(&replay, b"fixture", &candidate)
            .await
            .expect("readback"),
        receipt
    );
    assert!(!db.queue_contains(replay.id).await.expect("queue readback"));
    assert!(
        db.blob(replay.nzb_blob)
            .await
            .expect("blob readback")
            .is_none()
    );
    let failed = infinidysk_db::MigrationSubmission {
        intent: "failed-fixture-intent".into(),
        ..receipt.clone()
    };
    assert!(
        db.enqueue_migration(&job, b"fixture", &failed)
            .await
            .is_err()
    );
    assert!(
        db.migration_submission(&failed.intent)
            .await
            .expect("rolled back receipt")
            .is_none()
    );
    db.remove_job(job.id).await.expect("remove fixture");
    assert_eq!(
        db.migration_submission(&receipt.intent)
            .await
            .expect("independent receipt"),
        Some(receipt)
    );
}

async fn mapped_source_fences(db: &Db) {
    let root = db
        .item(ItemId::CONTENT)
        .await
        .expect("mapping root")
        .expect("mapping root");
    let mut item = Item::child(&root, "mapped-source-fixture.bin", ItemKind::NzbFile)
        .expect("mapping fixture");
    let meta = BlobId::new();
    item.meta_blob = Some(meta);
    item.size = Some(7);
    db.import_items_with_sources(
        &[(item.clone(), HealthStatus::default())],
        &[(meta, b"fixture")],
        &[(item.id, "original-evidence".into())],
    )
    .await
    .expect("mapping fixture");
    assert!(
        !db.capture_import_source(
            item.id,
            meta,
            8,
            b"fixture",
            "original-evidence",
            "wrong-size"
        )
        .await
        .expect("size fence")
    );
    assert!(
        !db.capture_import_source(
            item.id,
            meta,
            7,
            b"changed",
            "original-evidence",
            "wrong-bytes"
        )
        .await
        .expect("metadata fence")
    );
    assert!(
        !db.capture_import_source(
            item.id,
            meta,
            7,
            b"fixture",
            "stale-evidence",
            "wrong-version"
        )
        .await
        .expect("provenance fence")
    );
    assert!(
        db.capture_import_source(
            item.id,
            meta,
            7,
            b"fixture",
            "original-evidence",
            "derived-evidence"
        )
        .await
        .expect("capture evidence")
    );
    db.set_health(
        item.id,
        &HealthStatus {
            state: HealthState::Hidden,
            ..HealthStatus::default()
        },
        Recheck::None,
    )
    .await
    .expect("source removed");
    assert!(
        !db.capture_import_source(
            item.id,
            meta,
            7,
            b"fixture",
            "derived-evidence",
            "hidden-source"
        )
        .await
        .expect("hidden fence")
    );
    assert_eq!(
        db.import_source(item.id)
            .await
            .expect("read proof")
            .as_deref(),
        Some("derived-evidence")
    );
    db.delete_item(item.id)
        .await
        .expect("remove mapping fixture");
    assert!(
        db.import_source(item.id)
            .await
            .expect("proof cascade")
            .is_none()
    );
}

async fn legacy_repair_holds(db: &Db) {
    let item = ItemId::new();
    let other = ItemId::new();
    assert!(
        !db.legacy_repair_held(item)
            .await
            .expect("legacy hold fixture")
    );
    db.retain_legacy_repair_hold("fixture-evidence", Some(item), "arr-request", "immutable")
        .await
        .expect("legacy hold fixture");
    db.retain_legacy_repair_hold("fixture-evidence", Some(other), "changed", "changed")
        .await
        .expect("legacy hold fixture");
    assert!(
        db.legacy_repair_held(item)
            .await
            .expect("legacy hold fixture")
    );
    assert!(
        !db.legacy_repair_held(other)
            .await
            .expect("legacy hold fixture")
    );
    db.retain_legacy_repair_hold("fixture-unbound", None, "link-removal", "intent")
        .await
        .expect("legacy hold fixture");
    assert!(
        db.legacy_repair_held(other)
            .await
            .expect("legacy hold fixture")
    );
    db.observe_legacy_repair("fixture-unbound", Some("replacement observed"))
        .await
        .expect("resolve unbound");
    assert!(
        !db.legacy_repair_held(other)
            .await
            .expect("resolved unbound")
    );
    db.observe_legacy_repair("fixture-evidence", Some("replacement observed"))
        .await
        .expect("observe bound");
    assert!(
        db.legacy_repair_held(item)
            .await
            .expect("original ID remains protected")
    );
}

/// A probe write that waits on an uncommitted delete of its item is a no-op once the
/// delete commits and lands once it rolls back. Needs `INFINIDYSK_TEST_POSTGRES_URL`.
#[tokio::test]
async fn postgres_probe_writes_race_item_deletes() {
    use std::time::Duration;
    let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
        eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
        return;
    };
    let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
    let admin = sqlx::PgPool::connect(&admin_url)
        .await
        .expect("connect admin");
    let create = sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}"));
    sqlx::query(create).execute(&admin).await.expect("create");
    let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
    let url = format!("{base}/{name}");
    let db = Db::connect(&url).await.expect("connect");
    let raw = sqlx::PgPool::connect(&url).await.expect("connect raw");
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let dir = Item::child(&content, "race", ItemKind::Directory).expect("valid");
    let keep = Item::child(&dir, "keep.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(&[dir.clone(), keep], &[]).await.expect("insert");
    let defect = Defect {
        offset: 8,
        size: 4,
        seen_at: 9,
        source: -1,
    };

    for (n, (structural, commit)) in [(false, true), (false, false), (true, true), (true, false)]
        .into_iter()
        .enumerate()
    {
        let case = format!("structural={structural} commit={commit}");
        let file = Item::child(&dir, &format!("{n}.mkv"), ItemKind::NzbFile).expect("valid");
        db.insert(std::slice::from_ref(&file), &[])
            .await
            .expect("insert");

        let mut a = raw.begin().await.expect("begin");
        let a_pid: i32 = sqlx::query_scalar("SELECT pg_backend_pid()")
            .fetch_one(&mut *a)
            .await
            .expect("pid");
        sqlx::query("DELETE FROM items WHERE id = $1")
            .bind(file.id.0)
            .execute(&mut *a)
            .await
            .expect("delete");

        let b_db = db.clone();
        let id = file.id;
        let b = tokio::spawn(async move {
            if structural {
                b_db.set_structural(id, Some(&defect), true)
                    .await
                    .map(|()| true)
            } else {
                b_db.request_media_probe(id, 7).await
            }
        });

        let blocked = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity \
                       WHERE datname = current_database() \
                       AND query LIKE 'INSERT INTO media_probes%' \
                       AND pg_blocking_pids(pid) @> ARRAY[$1::int])";
        let waited = tokio::time::timeout(Duration::from_secs(10), async {
            while !sqlx::query_scalar::<_, bool>(blocked)
                .bind(a_pid)
                .fetch_one(&raw)
                .await
                .expect("observe")
            {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await;
        assert!(waited.is_ok(), "{case}: the write waits on the delete");

        if commit {
            a.commit().await.expect("commit");
        } else {
            a.rollback().await.expect("rollback");
        }
        let accepted = tokio::time::timeout(Duration::from_secs(10), b)
            .await
            .expect("the write finishes")
            .expect("join")
            .unwrap_or_else(|e| panic!("{case}: {e}"));

        let rows: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM media_probes WHERE item_id = $1")
            .bind(file.id.0)
            .fetch_one(&raw)
            .await
            .expect("count");
        let probe = db.media_probe(file.id).await.expect("query");
        if commit {
            assert!(db.item(file.id).await.expect("query").is_none(), "{case}");
            assert_eq!(rows, 0, "{case}: nothing is left behind");
            if !structural {
                assert!(!accepted, "{case}");
            }
        } else if structural {
            assert_eq!(
                (probe.defect, probe.structural_hold),
                (Some(defect), true),
                "{case}"
            );
        } else {
            assert!(accepted, "{case}");
            assert_eq!(probe.requested_at, Some(7), "{case}");
        }
    }

    raw.close().await;
    db.close().await;
    let drop = sqlx::AssertSqlSafe(format!("DROP DATABASE {name}"));
    sqlx::query(drop).execute(&admin).await.expect("drop");
}

fn temp_sqlite() -> std::path::PathBuf {
    std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()))
}

fn remove_set(path: &std::path::Path) {
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

/// Runs `op` while another connection holds an uncommitted write, then commits it.
/// A cleanup that reads in a deferred transaction fails here with code 517.
async fn under_contention<T: Send + 'static>(
    url: &str,
    op: impl Future<Output = Result<T, infinidysk_db::Error>> + Send + 'static,
) -> T {
    let raw = sqlx::SqlitePool::connect(url).await.expect("open");
    sqlx::query("CREATE TABLE IF NOT EXISTS contention (x INTEGER)")
        .execute(&raw)
        .await
        .expect("create");
    let mut tx = raw.begin().await.expect("begin");
    sqlx::query("INSERT INTO contention VALUES (1)")
        .execute(&mut *tx)
        .await
        .expect("write");
    let op = tokio::spawn(op);
    tokio::time::sleep(std::time::Duration::from_millis(200)).await;
    tx.commit().await.expect("commit");
    let out = op
        .await
        .expect("join")
        .expect("cleanup survives the other writer");
    raw.close().await;
    out
}

#[tokio::test]
async fn cleanup_survives_a_concurrent_sqlite_writer() {
    let path = temp_sqlite();
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let cat = Item::child(&content, "contention", ItemKind::Directory).expect("valid");
    let job = Item::child(&cat, "job", ItemKind::Directory).expect("valid");
    let file = Item::child(&job, "a.mkv", ItemKind::NzbFile).expect("valid");
    let folder = Item::child(&cat, "nofiles", ItemKind::Directory).expect("valid");
    let unowned = Item::child(&content, "unowned.mkv", ItemKind::NzbFile).expect("valid");
    let empty = Item::child(&cat, "empty", ItemKind::Directory).expect("valid");
    db.insert(
        &[
            cat,
            job,
            file.clone(),
            folder.clone(),
            unowned.clone(),
            empty,
        ],
        &[],
    )
    .await
    .expect("insert");
    let before = infinidysk_db::now_ms() + 1;

    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_unowned(unowned.id).await }).await);
    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_without_files(folder.id).await }).await);
    let d = db.clone();
    assert!(
        under_contention(
            &url,
            async move { d.delete_unlinked(file.id, before).await }
        )
        .await
    );
    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_empty_dirs(before).await }).await >= 1);

    db.close().await;
    remove_set(&path);
}

#[tokio::test]
async fn backup_into_copies_a_consistent_database() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    let file = Item::child(&dir, "a.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(&[dir, file], &[]).await.expect("insert");
    db.write_config(&[("backup.test", "\"kept\"")], &[])
        .await
        .expect("config");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("open");
    let count: i64 = sqlx::query_scalar("SELECT count(*) FROM items")
        .fetch_one(&pool)
        .await
        .expect("count");
    pool.close().await;
    let copied = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open copy");
    let kept: Option<String> =
        sqlx::query_scalar("SELECT value FROM config WHERE key = 'backup.test'")
            .fetch_optional(&copied)
            .await
            .expect("config row");
    copied.close().await;
    assert_eq!(kept.as_deref(), Some("\"kept\""));
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert_eq!(check.incompatible, None);
    assert!(check.problems.is_empty(), "{:?}", check.problems);
    assert_eq!(check.items, u64::try_from(count).expect("positive"));
    assert_eq!(check.schema_version, infinidysk_db::latest_sqlite_schema());
    db.close().await;
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn backup_into_stops_when_asked() {
    let path = temp_sqlite();
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    let pool = sqlx::SqlitePool::connect(&url).await.expect("open");
    sqlx::query(
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 50000) \
         INSERT INTO config (key, value) SELECT 'k' || i, 'v' FROM n",
    )
    .execute(&pool)
    .await
    .expect("seed");
    pool.close().await;
    let copy = temp_sqlite();
    assert!(matches!(
        db.backup_into(&copy, || true).await,
        Err(Error::Interrupted)
    ));
    db.close().await;
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_refuses_a_newer_database() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    db.close().await;
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open");
    sqlx::query(
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         VALUES ($1, 'next', TRUE, X'00', 0)",
    )
    .bind(infinidysk_db::latest_sqlite_schema() + 1)
    .execute(&pool)
    .await
    .expect("insert");
    pool.close().await;
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("newer")),
        "{check:?}"
    );
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_refuses_an_unfinished_migration() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    db.close().await;
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open");
    sqlx::query("UPDATE _sqlx_migrations SET success = FALSE WHERE version = $1")
        .bind(infinidysk_db::latest_sqlite_schema())
        .execute(&pool)
        .await
        .expect("update");
    pool.close().await;
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("did not finish")),
        "{check:?}"
    );
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_names_a_1x_database() {
    let path = temp_sqlite();
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}?mode=rwc", path.display()))
        .await
        .expect("open");
    sqlx::query(r#"CREATE TABLE "__EFMigrationsHistory" (x)"#)
        .execute(&pool)
        .await
        .expect("create");
    pool.close().await;
    let check = infinidysk_db::check_file(&path, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("1.x")),
        "{check:?}"
    );
    std::fs::write(&path, vec![7u8; 8192]).expect("write");
    let check = infinidysk_db::check_file(&path, || false)
        .await
        .expect("check");
    assert!(check.incompatible.is_some(), "{check:?}");
    remove_set(&path);
}

async fn legacy_source_retirement(db: &Db) {
    let root = db.item(ItemId::CONTENT).await.expect("root").expect("root");
    let mut item = Item::child(&root, "retire-fixture.bin", ItemKind::NzbFile).expect("item");
    let blob = BlobId::new();
    item.meta_blob = Some(blob);
    item.size = Some(1);
    let provenance = "original-generation";
    db.import_items_with_sources(
        &[(item.clone(), HealthStatus::default())],
        &[(blob, b"original")],
        &[(item.id, provenance.into())],
    )
    .await
    .expect("import");
    let evidence = format!("retirement-{}", item.id);
    db.retain_legacy_repair_request(&evidence, Some(item.id), "arr-request", "immutable", None)
        .await
        .expect("hold");
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"original", provenance, "proof")
            .await
            .expect("unresolved")
    );
    db.observe_legacy_repair(&evidence, Some("observed"))
        .await
        .expect("proof");
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"changed", provenance, "proof")
            .await
            .expect("changed bytes")
    );
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"original", "other-generation", "proof")
            .await
            .expect("changed provenance")
    );
    let mut changed = item.clone();
    changed.created_at += 1;
    assert!(
        !db.retire_legacy_source(&evidence, &changed, b"original", provenance, "proof")
            .await
            .expect("changed lifetime")
    );
    changed = item.clone();
    changed.path.push('x');
    assert!(
        !db.retire_legacy_source(&evidence, &changed, b"original", provenance, "proof")
            .await
            .expect("changed path")
    );
    changed = item.clone();
    changed.size = Some(2);
    assert!(
        !db.retire_legacy_source(&evidence, &changed, b"original", provenance, "proof")
            .await
            .expect("changed size")
    );
    assert!(
        db.wake_legacy_retirement(item.id)
            .await
            .expect("last-reader wake")
    );
    assert!(
        db.legacy_retirement_candidates(0, 100)
            .await
            .expect("urgent candidates")
            .iter()
            .any(|r| r.evidence_id == evidence)
    );
    db.touch_legacy_retirement(&evidence)
        .await
        .expect("bounded attempt checkpoint");
    let other = format!("unresolved-{}", item.id);
    db.retain_legacy_repair_hold(&other, None, "link-removal", "uncertain")
        .await
        .expect("global hold");
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"original", provenance, "proof")
            .await
            .expect("global uncertainty")
    );
    db.observe_legacy_repair(&other, Some("observed"))
        .await
        .expect("global proof");
    let intent = db
        .prepare_regrab(item.id, "fixture", "{}")
        .await
        .expect("new repair");
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"original", provenance, "proof")
            .await
            .expect("active repair")
    );
    db.checkpoint_regrab(item.id, "fixture", intent.revision, "{}", true)
        .await
        .expect("terminal repair");
    assert!(
        db.retire_legacy_source(&evidence, &item, b"original", provenance, "fresh proof")
            .await
            .expect("retire")
    );
    assert!(db.item(item.id).await.expect("missing").is_none());
    let audit = db.legacy_repair_status().await.expect("audit");
    let audit = audit
        .iter()
        .find(|r| r.evidence_id == evidence)
        .expect("retained audit");
    assert!(audit.retired_at.is_some());
    assert!(!audit.source_present);
    assert!(
        db.legacy_repair_held(item.id)
            .await
            .expect("retained evidence")
    );
    assert!(
        !db.retire_legacy_source(&evidence, &item, b"original", provenance, "proof")
            .await
            .expect("idempotent")
    );
}

async fn library_item_pages(db: &Db) {
    use infinidysk_db::catalogue::Link;
    let wanted = uuid::Uuid::new_v4();
    let other = uuid::Uuid::new_v4();
    let root = "/item-pages-fixture";
    let links: Vec<_> = (0..5)
        .map(|i| Link {
            root: root.into(),
            path: format!("{root}/{i}.mkv"),
            relative_path: format!("{i}.mkv"),
            group_name: if i < 3 { "first" } else { "second" }.into(),
            item_id: if i == 1 { other } else { wanted },
            pointer_hash: "fixture".into(),
            link_kind: 1,
            video: i16::from(i != 3),
        })
        .collect();
    db.catalogue_replace(root, "fixture", &links, 1, 0)
        .await
        .expect("snapshot");
    let first = db
        .catalogue_links(None, None, Some(wanted), true, "", 2)
        .await
        .expect("item page");
    assert_eq!(first, vec![links[0].clone(), links[2].clone()]);
    let next = db
        .catalogue_links(Some(root), None, Some(wanted), true, &first[1].path, 2)
        .await
        .expect("next item page");
    assert_eq!(next, vec![links[4].clone()]);
    let group = db
        .catalogue_links(Some(root), Some("second"), Some(wanted), false, "", 10)
        .await
        .expect("group");
    assert_eq!(group, links[3..].to_vec());
    let broad = db
        .catalogue_links(Some(root), None, None, false, "", 10)
        .await
        .expect("broad");
    assert_eq!(broad, links);
    let empty = db
        .catalogue_links(
            Some("/unconfigured-fixture"),
            None,
            Some(wanted),
            false,
            "",
            10,
        )
        .await
        .expect("empty");
    assert_eq!(empty, [] as [Link; 0]);
    db.catalogue_replace(root, "fixture", &[], 2, 1)
        .await
        .expect("cleanup fixture");
}
