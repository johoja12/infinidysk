# Fork feature migration to Rust 2.x

This is the implementation backlog for `feat/rust-v2-fork-migration`. It starts from
upstream Rust `main` at `b16e071ed60815d34cdb9a84ccc60695bceb7fea` (2.0.0-beta.1 line).
The behavior specification is fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
The inventory was derived from the fork's non-merge commits and file differences
against upstream `v1` at `4a630deb6e599f3eadbe071a92417a93d74dc81f` on 2026-11-03.
It groups related changes into capabilities; it is not a promise of complete parity.

The 1.x source and production deployment remain unchanged. Port behavior into the
Rust architecture; do not copy C# storage formats or add a second cache in front of
FUSE/WebDAV. Keep 1.x synchronization pointed at upstream `v1`. Rust changes follow
upstream `main`. Do not merge this migration into the production branch until the
acceptance gates below pass.

## Status and order

- **Port**: a fork behavior needs an implementation in Rust.
- **Verify**: upstream has a corresponding subsystem; parity needs tests or inspection.
- **Replace**: the old mechanism does not fit 2.x; preserve its user outcome.
- P0 blocks a production trial; P1 preserves the main fork workflows; P2 follows those.
- No row is complete merely because a similarly named Rust module exists.

| ID | Priority | Capability and 1.x specification | Rust starting point / work required | Status |
| --- | --- | --- | --- | --- |
| R01 | P0 | Replace an exactly identified damaged import even when its original grab is absent (`ArrClient`, `RadarrClient`, `HealthCheckService`; fork PR #230) | `crates/arr/src/repair.rs` already deletes/searches without a grab, but unnecessarily waits for an automatic search it never triggered. Skip that wait and retain replacement/error guards. | First port implemented; validation recorded below |
| R02 | P0 | Recover the original download from exact file ID + imported path; reject ambiguity and incomplete history (`ArrDownloadIdResolver`, fork PR #234) | `repair.rs::owner_of_file` and Arr owner backfill exist. Add scoped, fully paginated Radarr history using **`movieIds`**, verify current identity, and carry recovered identity into blocklisting. Do not use a migration job ID as original provenance. Sonarr filtering stays profile-specific. | Port |
| R03 | P0 | Protect live/overlapping reads, but bypass recent-read delay for confirmed damage after readers close (`ConcurrentReadTracker`, fork PR #232) | `health::Health::with_in_use` and VFS read handles exist. Prove FUSE/WebDAV/STRM accounting, cancellation cleanup, and no destructive repair while any reader remains. | Verify |
| R04 | P0 | Keep read/warm-triggered damaged-file repairs urgent through provider circuit outages (`HealthCheckService`, `PrefetchRepairHistory`, `eafe1705`) | `crates/health`, `crates/nntp` circuit handling; persist priority and bounded retry without treating an open circuit as conclusive missing articles. Warming integration depends on P01. | Port |
| R05 | P0 | Resumable imported-content validation and fair forced rechecks (`ImportedContentSweepService`, `IFileRecheckQueue`) | Rust health sampling/rechecks exist. Compare scheduling fairness, known-hole BODY probes, durable cursor, quotas and cancellation. | Verify / port gaps |
| R06 | P0 | Shared regrab intent/outcome tracking, guarded link removal, detached replacement observation (`Services/Regrab`, `ArrRegrab` API) | Rust health states and `arr::replace` exist; add durable reconciliation for partial/uncertain Arr writes and library-root ownership checks. A non-2xx DELETE may already have removed the link; reconcile before retry. | Port |
| R07 | P0 | Bounded PAR2 repair attempts without holding the health worker (`Par2RepairService`, `0a3bba3a`, `42f09136`) | `crates/par2` currently parses indexes and verifies slices; this baseline has no equivalent recovery executor in health. Port trusted range/slice identity, bounded repair budgets, durable outcomes and separate worker scheduling before declaring repair parity. | Port |
| C01 | P0 | Native final-media cache with verified block coverage and source revisions (`Services/NativeCache`, `NativeCachedStream`) | `crates/stream/{cache,disk}.rs` caches decoded articles, not the fork's final-media chunks. Design a shared VFS/stream cache interface before implementing final-media retention. | Port; design first |
| C02 | P0 | Cache folder ownership, mount identity, quota/free-space reserves, watermarks, read-only folders, safe eviction and pinning (`NativeCacheStore`, `NativeFileSystem`) | Rust disk cache has capacity/age eviction; multi-folder placement, file retention and NAS failure behavior need parity. Never reuse legacy cache roots writable during a trial. | Port |
| C03 | P0 | Repair revisions, generation-safe multipart identity, invalidation after replacement/removal (`ContentRevisionTracker`, `RepairRevisionStore`, `MultipartContentIdentity`) | New Rust metadata/blob versions and article cache identities require explicit mapping. Test stale revision exclusion and concurrent eviction/read behavior. | Port |
| C04 | P1 | Progressive cold reads, bounded first-byte fill, coalesced fills, source window reuse (`NativeCachedStream`, `NativeSourceCacheStream`; `c6a70ba8`, `0c3c129f`) | Rust segment cache already coalesces in-flight reads. Benchmark FUSE/WebDAV seeks before adding buffering; preserve foreground priority and bounded memory/I/O under NAS stalls. | Verify / port gaps |
| C05 | P1 | Native cache browser, verified ranges, eviction history, buffer/I/O diagnostics (`NativeCacheBrowser*`, cache APIs) | Rust cache statistics exist; final-media file/range APIs and catalogue UI depend on C01–C03. Queries must not warm files or scan NAS payloads. | Port |
| P01 | P1 | Durable Smart Prefetch queue, policies, pause/resume/cancel, budgets, bounded lanes (`Services/Prefetch`, `PrefetchJobStore`) | Rust stream read-ahead is not the fork's background warming scheduler. Build a separate scheduler using the shared cache and provider workload classes. | Port |
| P02 | P1 | Plex account/PIN/Home-user authentication, server discovery and mapping (`Services/Plex`, Plex API) | No equivalent Plex account/catalogue service found in the Rust baseline. Keep credentials masked and all provider/account access explicit. | Port |
| P03 | P1 | Viewer-scoped next-episode and partially watched predictions; durable snapshots during Plex outages (`PredictionSnapshot*`, `PlexViewerResolver`) | Depends on P02 and the media catalogue. Preserve account identity validation, viewer exclusions and uncertainty states. | Port |
| P04 | P1 | Playback-aware warming priority, viewer lead, own-file warm lane, urgent backfills, cooperative yields (`WarmYieldPolicy`, `WarmConnectionGovernor`, `PrefetchWireBudget`) | Depends on P01 and reader tracking. Cached playback must not consume provider allowance; warming cannot starve an uncached seek. | Port |
| P05 | P1 | Warming history by source revision/range, fetched bytes, source attribution and repair outcomes (`PrefetchRepairHistory`, `WarmingRateWindow`) | Depends on C01, P01 and R06. Use Rust decimal-byte API/UI conventions. | Port |
| L01 | P1 | Persisted library link index, additional scan roots, video filter, catalogue/grouped browse (`Services/Library`, `LibraryLinkMaps`) | Rust library conversion/reconciliation exists, but not the fork catalogue and grouping. Preserve exact source-to-link mapping and bounded scans. | Port |
| L02 | P1 | Plex metadata matching and indexed library classification (`PlexLibraryMetadataService`, `LibraryCatalogService`) | Depends on P02/L01. Keep matching separate from authority to delete or regrab a file. | Port |
| L03 | P1 | File details: source identity, current cache ranges, health history, active regrab, manual regrab (`GetLibraryFileDetails`, file modal) | Compose new Rust admin APIs over L01/C05/R06; do not assume 1.x API compatibility. | Port |
| L04 | P1 | Live playback rows grouped across bursts, imported file ID, viewer attribution and cache-served reads (`ActiveReadRegistry`, `ActiveReadsBroadcaster`) | Rust `vfs::session` and traces exist; add only missing correlation/attribution and prove cleanup on reader drop. | Verify / port gaps |
| M01 | P0 | Import our actual fork data without losing ownership or repair state (`NzbDavMigration`, fork DB additions) | `crates/import` imports C# data, but fork tables/settings/cache layouts need an explicit compatibility report. Inventory unsupported settings and records; fail or report them rather than silently discard. | Verify / port gaps |
| M02 | P1 | Legacy NZB catalogue recovery, digest-bound mapped export packages and parallel canary mount (`tools/NzbDavMigration`) | Keep existing tools read-only initially. Decide whether to retain the external tool or add a Rust adapter; preserve package digest, source identity and rollback journal. | Replace / adapter |
| M03 | P1 | Pipelined batches, separate validation slot, resumable priority schedule and failed-import recovery (`NzbDavMigrationController`, `UsenetMigration`) | New Rust queue/admin contracts need adapters and restart-safe checkpoints. No acknowledgement without terminal proof. | Port |
| M04 | P1 | Resumable external import runner, systemd service and Plex-hub priority ordering (`scripts/operations/plex-import-runner.py`, `plex_hub_priority.py`) | Adapt the operational tools to the new Rust import/admin contracts. Keep existing journals immutable; prove restart, duplicate-submission and priority-snapshot behavior against a separate canary. | Replace / adapter |
| S01 | P0 | Completed-link removal remains removed across restart (`DatabaseStore`, `3e981833`) | Rust VFS/database cleanup has different links; add protocol + restart tests for `/completed-symlinks` and source ownership. | Verify |
| S02 | P1 | Bounded cancelled-body cleanup and low-latency multipart reads (`UsenetSharp`, `NativeSourceCacheStream`; `963f3090`, `fe73df69`) | Rust uses Drop/lease guards, ordered pipelines and shared streaming. Verify connection/permit release and cold seek benchmarks; do not mechanically copy timeout values. | Verify |
| S03 | P1 | Validate archived media before import completion; preserve first-volume order (`Queue`, `fe0dd0f2`, `c30fd88e`) | Rust queue/archive analyzer already exists. Port malformed-media and multipart ordering regression scenarios. | Verify |
| O01 | P0 | Exact source/image provenance and fork version in builds (`scripts/build-prod-image.sh`, `248bd117`) | Rust Docker/release pipeline uses its own embedding. Add fork identity without publishing stable upstream tags; require image/source receipt for trial. | Verify / port gaps |

The paths in the specification column are on the pinned 1.x fork, not on this branch.
The rows cover user-visible fork additions and the recent playback/repair fixes. They
exclude upstream-only 1.x features and dependency/test-only commits. Further code
review may split rows or identify gaps; mark them here rather than claiming parity.

## First increment: missing-grab repair

R01 carries the fork's recovery behavior into Rust: a known current media file can be
removed and searched again when the original grab history is gone. The existing
Rust code already takes that route, but waits for the blocklist-induced automatic
search even when no blocklist request was made. That delay is removed. A queued or
imported replacement still prevents mutation. A failed blocklist or deletion stops
the operation. No configuration or setup-wizard fields change.

Loopback HTTP tests cover Radarr, Sonarr, movie-shaped Whisparr, a queued replacement,
an imported replacement, blocklist/deletion failures, and file-based ownership with
no known download. These tests exercise the Rust API; they do not certify live Arr,
provider access, migration, performance, or production readiness.

## Next increments

1. R02: original-download provenance and pagination, including ambiguous imports,
   missing file IDs, changed paths, partial pages, deletion history and profile-specific
   query contracts. Keep unknown identity explicit; never blocklist a guessed release.
2. R03/R06: reader safety and durable partial-write reconciliation before expanding
   automatic remediation. Add a fake Arr case that removes a file then returns 500.
3. C01–C03 design and a small shared-cache slice with durability/revision tests.
4. P01/P02 and L01, then prediction and UI layers. Migration adapters are developed
   alongside the features whose state they import.

## Trial and production gates

- Run Rust formatting, workspace clippy and workspace tests with the pinned toolchain
  and lockfile; exercise SQLite and PostgreSQL. Build the native release and UI.
- Import a copied, scrubbed fork configuration into a separate database. Produce an
  explicit carried/skipped/unsupported state report. Do not share writable config,
  blobs, cache roots, library destinations or download-client identity with production.
- Test SAB/WebDAV contracts, FUSE permissions/links, replacement races, account secret
  handling, and crash/restart recovery using fixtures and a separate canary mount.
- Measure uncached startup and seeks, warm reads, abandoned seeks, concurrent viewers
  and warming starvation with controlled source/cache state. No inferred percentages.
- Verify all P0 rows and document accepted P1 gaps before seeking a production cutover.
  Keep the 1.x image/config/cache rollback path. Port work alone authorizes no deployment.

## Initial validation receipt

Pinned toolchain: Rust 1.99.1. The three missing-grab integration cases failed on
unmodified upstream repair code (the requested 60-second grace period exceeded the
2-second assertion timeout). With the port, all 14 Arr tests passed, including nine
loopback repair integration cases. Workspace formatting and Clippy with `-D warnings`
passed. `cargo test --workspace --locked` passed: 296 tests, no failures; one ignored test.
The optional PostgreSQL test returns without exercising PostgreSQL when its URL is
unset, as in this local run. No live Arr/provider acceptance, UI production build,
release image, migration rehearsal or performance qualification is claimed.
