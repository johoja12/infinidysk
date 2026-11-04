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
fn provider(server: &LoopbackServer, downloads: &Arc<Downloads>) -> Provider {
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
        downloads.clone(),
    )
    .expect("pool");
    Provider {
        pool,
        key: "fixture".into(),
        name: "fixture".into(),
        priority: 0,
        backbone: None,
    }
}
fn provider_client(server: &LoopbackServer) -> (Arc<Client>, Arc<Downloads>) {
    let downloads = Downloads::new(8);
    (
        Arc::new(Client::new([provider(server, &downloads)])),
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

#[test]
fn large_slices_reserve_for_actual_damage_and_cancel_errors_have_one_public_outcome() {
    use md5::{Digest as _, Md5};
    fn packet(set: [u8; 16], kind: &[u8; 16], body: &[u8]) -> Vec<u8> {
        let mut rest = [set.as_slice(), kind, body].concat();
        rest.resize(rest.len().next_multiple_of(4), 0);
        [
            b"PAR2\0PKT".as_slice(),
            &(32 + rest.len() as u64).to_le_bytes(),
            &Md5::digest(&rest),
            &rest,
        ]
        .concat()
    }
    let id = [1; 16];
    let set_id = [42; 16];
    let size = 32u32 << 20;
    let length = 8 * u64::from(size);
    let mut metadata = packet(
        set_id,
        b"PAR 2.0\0Main\0\0\0\0",
        &[
            u64::from(size).to_le_bytes().as_slice(),
            &1u32.to_le_bytes(),
            &id,
        ]
        .concat(),
    );
    metadata.extend(packet(
        set_id,
        b"PAR 2.0\0FileDesc",
        &[
            id.as_slice(),
            &[0; 32],
            &length.to_le_bytes(),
            b"fixture.bin",
        ]
        .concat(),
    ));
    metadata.extend(packet(
        set_id,
        b"PAR 2.0\0IFSC\0\0\0\0",
        &[id.as_slice(), &[0; 8 * 20]].concat(),
    ));
    let set = RecoverySet::read(&metadata, None)
        .expect("bounded synthetic geometry; no payloads allocated");
    let source = Source {
        segments: Segments {
            items: (0..8)
                .map(|i| Segment {
                    id: format!("large-{i}@loopback"),
                    size,
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            ..Segments::default()
        },
        proof: Par2Proof {
            file_id: id,
            file_md5: [0; 16],
            md5_16k: [0; 16],
            file_length: length,
            slice_size: size,
            slice_md5: vec![[0; 16]; 8],
            slice_crc32: vec![0; 8],
        },
    };
    let recovery = Segments {
        items: vec![Segment {
            id: "recovery@loopback".into(),
            size: 1000,
            fallbacks: vec![],
        }],
        sizes_trusted: true,
        ..Segments::default()
    };
    assert!(
        Plan::new(set, vec![source], vec![recovery], Budget::default()).is_ok(),
        "one damaged large slice fits; eight is a policy ceiling, not a reservation"
    );
    assert!(matches!(
        Error::from(infinidysk_par2::repair::Error::Cancelled),
        Error::Cancelled
    ));
}

#[tokio::test]
async fn complete_articles_roundtrip_through_the_durable_store_after_whole_file_proof() {
    use infinidysk_health::par2::ArticleOutcome;
    use infinidysk_native_cache::repair::{Geometry, Patch, Proof, Store};
    use infinidysk_nntp::Workload;
    let (plan, articles, set) = setup(Budget::default(), true);
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, downloads) = provider_client(&server);
    let ArticleOutcome::Repaired(articles) = plan
        .execute_articles(client, CancellationToken::new())
        .await
        .expect("whole articles")
    else {
        panic!("repair expected");
    };
    assert_eq!(
        articles.len(),
        3,
        "all articles crossing the two restored slices are complete"
    );
    let path =
        std::env::temp_dir().join(format!("infinidysk-par2-articles-{}", uuid::Uuid::new_v4()));
    let store = Store::open(&path).await.expect("store");
    let patches: Vec<_> = articles
        .into_iter()
        .map(|article| {
            let (message_id, body, proof) = article.into_parts();
            assert_eq!(proof.set(), set.id());
            assert_eq!(proof.file(), set.files()[0].id);
            assert_eq!(proof.md5(), set.files()[0].hash);
            assert_eq!(body.file_size, set.files()[0].length);
            let expected = data(usize::try_from(body.file_size).expect("file size"));
            assert_eq!(
                body.data.as_ref(),
                &expected[usize::try_from(body.offset).expect("offset")
                    ..usize::try_from(body.offset).expect("offset") + body.data.len()]
            );
            Patch {
                message_id,
                geometry: Geometry {
                    offset: body.offset,
                    file_size: body.file_size,
                    part: body.part,
                    total: body.total,
                },
                proof: Proof {
                    set: proof.set(),
                    file: proof.file(),
                    file_md5: proof.md5(),
                    file_length: body.file_size,
                },
                data: body.data,
            }
        })
        .collect();
    let expected: Vec<_> = patches
        .iter()
        .map(|p| (p.message_id.clone(), p.data.clone()))
        .collect();
    store
        .publish(patches)
        .await
        .expect("atomic verified publication");
    let reader = Client::new([]);
    assert!(reader.set_repairs(store.clone()));
    for (id, bytes) in expected {
        assert_eq!(
            reader
                .body(&id, Workload::Health, None)
                .await
                .expect("provider-free repaired body")
                .data,
            bytes
        );
    }
    assert_eq!(downloads.available_permits(), 8);
    drop(reader);
    drop(store);
    std::fs::remove_dir_all(path).expect("cleanup");
}

async fn proof_request_count() -> usize {
    let (plan, articles, _) = setup(Budget::default(), true);
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, _) = provider_client(&server);
    assert!(matches!(
        plan.execute(client, CancellationToken::new())
            .await
            .expect("proof"),
        Outcome::Repaired(_)
    ));
    server.body_commands()
}
#[tokio::test]
async fn article_assembly_cannot_reset_the_request_allowance_after_verification() {
    let requests = proof_request_count().await;
    let (plan, articles, _) = setup(
        Budget {
            requests,
            ..Budget::default()
        },
        true,
    );
    let server = LoopbackServer::start(articles).await.expect("server");
    let (client, downloads) = provider_client(&server);
    assert!(matches!(
        plan.execute_articles(client, CancellationToken::new())
            .await,
        Err(Error::Budget)
    ));
    assert_eq!(
        server.body_commands(),
        requests,
        "no extra admission after the original allowance"
    );
    assert_eq!(downloads.available_permits(), 8);
}
#[tokio::test]
async fn changed_healthy_bytes_in_a_mixed_article_cannot_be_published() {
    use std::sync::{
        Mutex,
        atomic::{AtomicUsize, Ordering},
    };
    let requests = proof_request_count().await;
    let (plan, articles, set) = setup(Budget::default(), true);
    assert!(set.files()[0].length > 12_288);
    let server = LoopbackServer::start(articles).await.expect("server");
    let mut changed = data(usize::try_from(set.files()[0].length).expect("file size"));
    changed[8192] ^= 1;
    let changed_server = LoopbackServer::start(post("source-0", "fixture.bin", &changed, 3000))
        .await
        .expect("changed server");
    let (client, downloads) = provider_client(&server);
    let next = Mutex::new(Some(provider(&changed_server, &downloads)));
    let weak = Arc::downgrade(&client);
    let completed = AtomicUsize::new(0);
    client.set_sink(Box::new(move |_, _, _, _, _| {
        // One BODY was a missing article. Switch after the final successful BODY
        // of the original verification, before mixed-article assembly reads.
        if completed.fetch_add(1, Ordering::SeqCst) + 1 == requests - 1 {
            weak.upgrade().expect("client alive").replace([next
                .lock()
                .expect("provider")
                .take()
                .expect("one switch")]);
        }
    }));
    assert!(matches!(
        plan.execute_articles(client, CancellationToken::new())
            .await,
        Err(Error::Changed)
    ));
    assert!(
        changed_server.body_commands() > 0,
        "changed source was read during article assembly"
    );
    assert_eq!(downloads.available_permits(), 8);
}
