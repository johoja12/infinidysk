use infinidysk::config::{
    ArrInstance, ArrProfile, ConfigStore, Error, LibraryDir, MASK, Provider, Secret, Settings,
};
use infinidysk_db::Db;

#[tokio::test]
async fn env_overrides_win_are_never_stored_and_masked_secrets_survive_saves() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let env = [
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "123".to_owned()),
        ("UNRELATED".to_owned(), "x".to_owned()),
    ];
    let mut store = ConfigStore::load(db.clone(), env.clone())
        .await
        .expect("load");
    assert_eq!(
        store.settings().sab.api_key,
        Secret("123".into()),
        "string fields stay verbatim"
    );
    assert_eq!(store.locked().collect::<Vec<_>>(), ["sab.api_key"]);

    let mut settings = store.settings().clone();
    settings.providers.push(Provider {
        profile: Some("eweka".into()),
        password: Secret("pw".into()),
        ..Provider::default()
    });
    store.save(settings).await.expect("save");
    let provider = &store.settings().providers[0];
    assert_eq!(
        (provider.hostname.as_str(), provider.port),
        ("news.eweka.nl", 563)
    );
    assert_eq!(
        provider.backbone.as_deref(),
        Some("eweka"),
        "catalog fills the backbone"
    );

    // The UI round-trips masked settings; secrets must survive and never leak.
    let mut masked = store.settings().masked();
    assert_eq!(masked.providers[0].password, Secret(MASK.into()));
    assert!(!format!("{:?}", store.settings()).contains("pw"));
    masked.providers[0].priority = 1;
    store.save(masked.clone()).await.expect("save");
    assert_eq!(store.settings().providers[0].password, Secret("pw".into()));
    assert_eq!(store.settings().providers[0].priority, 1);

    masked.sab.api_key = Secret("changed".into());
    assert!(matches!(store.save(masked).await, Err(Error::Locked(p)) if p == "sab.api_key"));

    let keys: Vec<_> = db
        .config()
        .await
        .expect("query")
        .into_iter()
        .map(|(k, _)| k)
        .collect();
    assert_eq!(
        keys,
        ["providers"],
        "env values and defaults are not stored"
    );
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(reloaded.settings().sab.api_key, Secret::default());
    assert_eq!(reloaded.settings().providers, store.settings().providers);

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn out_of_range_stored_values_load_as_defaults_but_never_save() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    let mut settings = store.settings().clone();
    settings.providers.push(Provider {
        profile: Some("eweka".into()),
        password: Secret("pw".into()),
        ..Provider::default()
    });
    store.save(settings).await.expect("save");

    // As an older build or a hand edit could have stored them.
    let (_, raw) = db
        .config()
        .await
        .expect("query")
        .into_iter()
        .find(|(k, _)| k == "providers")
        .expect("providers stored");
    let mut providers: serde_json::Value = serde_json::from_str(&raw).expect("json");
    providers[0]["pipeline_depth"] = 99.into();
    db.write_config(
        &[
            ("providers", &providers.to_string()),
            ("sab", r#"{"manual_category":" "}"#),
        ],
        &[],
    )
    .await
    .expect("write");

    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    assert_eq!(store.settings().providers[0].pipeline_depth, 4);
    assert_eq!(store.settings().sab.manual_category, "uncategorized");

    let mut blank = store.settings().clone();
    blank.sab.manual_category = String::new();
    assert!(matches!(
        store.save(blank).await,
        Err(Error::Invalid { .. })
    ));

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[test]
fn library_dirs_stored_as_plain_paths_still_load() {
    let dirs: Vec<LibraryDir> =
        serde_json::from_str(r#"["/old", {"path": "/new", "arr_path": "/arr"}]"#).expect("parse");
    assert_eq!(
        dirs,
        [
            LibraryDir {
                path: "/old".into(),
                arr_path: String::new()
            },
            LibraryDir {
                path: "/new".into(),
                arr_path: "/arr".into()
            },
        ]
    );
}

#[test]
fn retired_experiments_are_dropped_on_load() {
    let settings: Settings =
        serde_json::from_str(r#"{"experiments": ["retired", "win-floor", "win-floor"]}"#)
            .expect("parse");
    assert_eq!(settings.experiments, ["win-floor"]);
}

#[test]
fn validation_rejects_unknown_catalog_entries_and_bad_arr_urls() {
    let custom = |backbone: Option<&str>| Settings {
        providers: vec![Provider {
            hostname: "news.example.com".into(),
            backbone: backbone.map(Into::into),
            ..Provider::default()
        }],
        ..Settings::default()
    };
    assert!(
        custom(None).validate().is_ok(),
        "a custom provider may be its own group"
    );
    assert!(custom(Some("eweka")).validate().is_ok());
    assert!(matches!(
        custom(Some("mine")).validate(),
        Err(Error::Invalid { .. })
    ));

    let mut wrong_host = Settings {
        providers: vec![Provider {
            profile: Some("eweka".into()),
            hostname: "news.example.com".into(),
            ..Provider::default()
        }],
        ..Settings::default()
    };
    assert!(matches!(wrong_host.validate(), Err(Error::Invalid { .. })));

    let old: Provider = serde_json::from_str(r#"{"hostname":"news.example.com"}"#).expect("json");
    assert_eq!(old.pipeline_depth, 4, "saved providers keep the old depth");
    for (depth, ok) in [(0, false), (1, true), (16, true), (17, false)] {
        let mut settings = custom(None);
        settings.providers[0].pipeline_depth = depth;
        assert_eq!(settings.validate().is_ok(), ok, "depth {depth}");
    }

    let mut arr = Settings {
        arr_instances: vec![ArrInstance {
            id: uuid::Uuid::now_v7(),
            name: "tv".into(),
            profile: ArrProfile::Sonarr,
            url: "ftp://arr".into(),
            api_key: Secret("k".into()),
            enabled: true,
        }],
        ..Settings::default()
    };
    assert!(matches!(arr.validate(), Err(Error::Invalid { .. })));
    arr.arr_instances[0].url = "http://sonarr:8989".into();
    assert!(arr.validate().is_ok());

    let mut network = Settings::default();
    network.network.trusted_proxies = vec![" 172.18.0.0/16 ".into(), String::new(), "::1".into()];
    assert!(network.validate().is_ok());
    assert_eq!(network.network.trusted_proxies, ["172.18.0.0/16", "::1"]);
    network.network.trusted_proxies.push("proxy.lan".into());
    assert!(matches!(network.validate(), Err(Error::Invalid { .. })));

    let mut strm = Settings::default();
    strm.imports.strategy = infinidysk::config::ImportStrategy::Strm;
    assert!(
        matches!(strm.validate(), Err(Error::Invalid { .. })),
        "STRM links need a public URL"
    );
    strm.network.public_url = "https://media.example/".into();
    assert!(strm.validate().is_ok());
    let old: Settings = serde_json::from_str("{}").expect("json");
    assert!(
        old.network
            .trusted_proxies
            .contains(&"192.168.0.0/16".to_owned())
    );

    let mut public = Settings::default();
    public.network.public_url = " https://media.example.com/infinidysk/ ".into();
    assert!(public.validate().is_ok());
    assert_eq!(
        public.network.public_url,
        "https://media.example.com/infinidysk"
    );
    public.network.public_url = "media.example.com".into();
    assert!(matches!(public.validate(), Err(Error::Invalid { .. })));
}

#[tokio::test]
async fn warming_settings_preserve_live_private_ledger_and_respect_environment_authority() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    db.write_config(&[("fork_prefetch_v1", "{\"generation\":1}")], &[])
        .await
        .expect("initial ledger");
    let env = [(
        "INFINIDYSK__PREFETCH__DAILY_FILL_BYTES".to_owned(),
        "20000000000".to_owned(),
    )];
    let mut store = ConfigStore::load(db.clone(), env).await.expect("load");
    db.write_config(&[("fork_prefetch_v1", "{\"generation\":2}")], &[])
        .await
        .expect("worker advances ledger");
    let mut next = store.settings().clone();
    next.prefetch.enabled = true;
    assert!(
        matches!(
            store.save(next).await,
            Err(Error::Invalid {
                path: "prefetch",
                ..
            })
        ),
        "warming requires native cache"
    );
    let mut next = store.settings().clone();
    next.prefetch.daily_fill_bytes = 10_000_000_000;
    assert!(
        matches!(store.save(next).await,Err(Error::Locked(p)) if p=="prefetch.daily_fill_bytes")
    );
    let mut next = store.settings().clone();
    next.sab.manual_category = "fixture".into();
    store.save(next).await.expect("unrelated settings save");
    let ledger = db
        .config()
        .await
        .expect("read config")
        .into_iter()
        .find(|(k, _)| k == "fork_prefetch_v1")
        .expect("private ledger")
        .1;
    assert_eq!(
        ledger, "{\"generation\":2}",
        "stale Settings snapshot must not rewrite the worker ledger"
    );
    db.close().await;
    let _ = std::fs::remove_file(path);
}

#[tokio::test]
async fn plex_classification_settings_preserve_defaults_selection_and_environment_authority() {
    let path = std::env::temp_dir().join(format!(
        "infinidysk-classification-config-{}.sqlite",
        uuid::Uuid::new_v4()
    ));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("database");
    let mut store = ConfigStore::load(
        db.clone(),
        [("INFINIDYSK__IMPORTS__PLEX_MATCHING".into(), "true".into())],
    )
    .await
    .expect("settings");
    assert!(store.settings().imports.plex_matching);
    assert!(store.settings().imports.plex_server_ids.is_none());
    let mut settings = store.settings().clone();
    settings.imports.plex_server_ids = Some(vec!["machine".into(), "machine".into()]);
    store.save(settings).await.expect("selection");
    assert_eq!(
        store
            .settings()
            .imports
            .plex_server_ids
            .as_ref()
            .expect("selection"),
        &["machine"]
    );
    let mut changed = store.settings().clone();
    changed.imports.plex_matching = false;
    assert!(matches!(store.save(changed).await,Err(Error::Locked(p))if p=="imports.plex_matching"));
    let restored = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert!(
        !restored.settings().imports.plex_matching,
        "env opt-in is never written as a stored default"
    );
    assert_eq!(
        restored
            .settings()
            .imports
            .plex_server_ids
            .as_ref()
            .expect("stored selection"),
        &["machine"]
    );
    let mut invalid = restored.settings().clone();
    invalid.imports.plex_server_ids = Some(vec!["invalid/identity".into()]);
    assert!(invalid.validate().is_err());
    db.close().await;
    drop(db);
    let _ = std::fs::remove_file(path);
}
