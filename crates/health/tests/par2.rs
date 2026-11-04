//! Provider-facing execution with independent par2cmdline recovery fixtures.
use infinidysk_core::{Par2Proof, Segment, Segments};
use infinidysk_health::par2::{Budget, Error, Outcome, Plan, Source};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_par2::repair::{Limits, RecoverySet};
use infinidysk_testkit::{Article, LoopbackServer, post};
use std::{sync::Arc, time::Duration};
use tokio_util::sync::CancellationToken;
fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/../par2/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture")
}
fn data(length: usize) -> Vec<u8> {
    (0..length)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect()
}
fn segments(articles: &[Article], length: usize, piece: usize) -> Segments {
    Segments {
        items: articles
            .iter()
            .enumerate()
            .map(|(i, a)| Segment {
                id: a.message_id.clone(),
                size: u32::try_from(piece.min(length - i * piece)).expect("article length"),
                fallbacks: vec![],
            })
            .collect(),
        sizes_trusted: true,
        ..Segments::default()
    }
}
fn setup(budget: Budget, remove: bool) -> (Plan, Vec<Article>, RecoverySet) {
    let set = RecoverySet::read(&fixture("set.par2"), None).expect("set");
    let mut sources = Vec::new();
    let mut served = Vec::new();
    for (i, file) in set.files().iter().enumerate() {
        let data = data(usize::try_from(file.length).expect("file length"));
        let mut articles = post(&format!("source-{i}"), "fixture.bin", &data, 3000);
        let ranges = segments(&articles, data.len(), 3000);
        sources.push(Source {
            segments: ranges,
            proof: Par2Proof {
                file_length: file.length,
                file_md5: file.hash,
                md5_16k: file.hash_16k,
                file_id: file.id,
                slice_size: u32::try_from(set.slice_size()).expect("slice size"),
                slice_md5: file.slices.iter().map(|s| s.md5).collect(),
                slice_crc32: file.slices.iter().map(|s| s.crc32).collect(),
            },
        });
        if i == 0 && remove {
            articles.remove(1);
        }
        served.extend(articles);
    }
    let recovery = fixture("set.vol0+3.par2");
    let articles = post("recovery", "fixture.par2", &recovery, 113);
    let volumes = vec![segments(&articles, recovery.len(), 113)];
    served.extend(articles);
    (
        Plan::new(set.clone(), sources, volumes, budget).expect("bound plan"),
        served,
        set,
    )
}
fn provider_client(server: &LoopbackServer) -> (Arc<Client>, Arc<Downloads>) {
    let downloads = Downloads::new(8);
    let pool = Pool::new(
        Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .expect("endpoint"),
        PoolConfig {
            max_connections: 2,
            min_connections: 0,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
        },
        Arc::clone(&downloads),
    )
    .expect("pool");
    (
        Arc::new(Client::new([Provider {
            pool,
            key: "fixture".into(),
            name: "fixture".into(),
            priority: 0,
            backbone: None,
        }])),
        downloads,
    )
}
#[tokio::test]
async fn missing_article_crosses_slice_boundaries_and_returns_whole_file_verified_bytes() {
    let (plan, articles, set) = setup(Budget::default(), true);
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, _) = provider_client(&server);
    let proof = plan
        .execute(client, CancellationToken::new())
        .await
        .expect("repair");
    let Outcome::Repaired(proof) = proof else {
        panic!("expected repair");
    };
    assert_eq!(proof.set(), set.id());
    let mut restored = Vec::new();
    for slice in proof.slices() {
        let at = set.locations()[slice.index];
        let original = data(usize::try_from(set.files()[at.file].length).expect("file length"));
        let start = at.local * set.slice_size();
        assert_eq!(slice.data, original[start..start + at.length]);
        restored.push(slice.index);
    }
    assert_eq!(restored, [0, 1]);
    assert!(
        server.body_commands() < 300,
        "one current article is reused across slice boundaries"
    );
}
#[tokio::test]
async fn missing_slice_and_read_budgets_stop_without_a_repair_proof() {
    let budget = Budget {
        reconstruction: Limits {
            missing: 1,
            ..Limits::default()
        },
        ..Budget::default()
    };
    let (plan, articles, _) = setup(budget, true);
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, _) = provider_client(&server);
    assert!(matches!(
        plan.execute(client, CancellationToken::new()).await,
        Err(Error::MissingLimit {
            observed: 2,
            maximum: 1
        })
    ));
    assert!(
        server.body_commands() < 5,
        "stop once the missing-slice ceiling is proved"
    );
    let (plan, articles, _) = setup(
        Budget {
            read_bytes: 2999,
            ..Budget::default()
        },
        true,
    );
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, _) = provider_client(&server);
    assert!(matches!(
        plan.execute(client, CancellationToken::new()).await,
        Err(Error::Budget)
    ));
    assert_eq!(server.body_commands(), 0);
}
#[tokio::test]
async fn provider_outages_are_inconclusive_and_cancellation_releases_transport_work() {
    let (plan, _, _) = setup(Budget::default(), true);
    assert!(matches!(
        plan.execute(Arc::new(Client::new([])), CancellationToken::new())
            .await,
        Err(Error::Provider(infinidysk_nntp::Error::Unavailable))
    ));
    let (plan, articles, _) = setup(Budget::default(), true);
    let (server, mut gate) = LoopbackServer::start_gated(articles).await.expect("server");
    let (client, downloads) = provider_client(&server);
    let token = CancellationToken::new();
    let handle = tokio::spawn(plan.execute(client, token.clone()));
    tokio::time::timeout(Duration::from_secs(5), gate.blocked())
        .await
        .expect("BODY admitted");
    token.cancel();
    assert!(matches!(
        handle.await.expect("worker"),
        Err(Error::Cancelled)
    ));
    gate.release();
    tokio::time::timeout(Duration::from_secs(5), async {
        while downloads.available_permits() != 8 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("permits returned after cancellation");
}
#[test]
fn partial_or_untrusted_import_ranges_do_not_become_repair_bindings() {
    let set = RecoverySet::read(&fixture("set.par2"), None).expect("set");
    let sources: Vec<_> = set
        .files()
        .iter()
        .map(|f| Source {
            segments: Segments::default(),
            proof: Par2Proof {
                file_length: f.length,
                file_md5: f.hash,
                md5_16k: f.hash_16k,
                file_id: f.id,
                slice_size: u32::try_from(set.slice_size()).expect("size"),
                slice_md5: f.slices.iter().map(|s| s.md5).collect(),
                slice_crc32: f.slices.iter().map(|s| s.crc32).collect(),
            },
        })
        .collect();
    assert!(matches!(
        Plan::new(set, sources, vec![Segments::default()], Budget::default()),
        Err(Error::Binding)
    ));
}

#[tokio::test]
async fn an_intact_source_is_reported_without_fetching_any_recovery_volume() {
    let (plan, articles, _) = setup(Budget::default(), false);
    let source_count = articles
        .iter()
        .filter(|a| a.message_id.starts_with("source-"))
        .count();
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, _) = provider_client(&server);
    assert!(matches!(
        plan.execute(client, CancellationToken::new()).await,
        Ok(Outcome::Healthy)
    ));
    assert_eq!(server.body_commands(), source_count);
}
