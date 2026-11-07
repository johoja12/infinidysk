# Fork feature migration to Rust 2.x

This is the implementation backlog for `feat/rust-v2-fork-migration`. It starts from
upstream Rust `main` at `b16e071ed60815d34cdb9a84ccc60695bceb7fea` (2.0.0-beta.1 line).
The behavior specification is fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
The inventory was derived from the fork's non-merge commits and file differences
against upstream `v1` at `4a630deb6e599f3eadbe071a92417a93d74dc81f` on 2026-11-03.
It groups related changes into capabilities; it is not a promise of complete parity.

**Current checkpoint:** the main repair, library, Smart Prefetch, native-cache and
migration workflows have Rust implementations. The latest increment adds guarded
retirement of replaced legacy sources and operational visibility of retained repair
evidence. Explicit streaming cancellation and archived-media parity regressions cover
the last verification rows. Qualification results and limits are recorded in
[final parity POC](validation/rust-v2-final-parity-poc-20261105.md).
This draft is not a production cutover. Live integration, production storage and
controlled performance acceptance remain distinct from generated local POC results.

The 1.x source and production deployment remain unchanged. Port behavior into the
Rust architecture while preserving existing storage compatibility where required;
do not add a second cache in front of FUSE/WebDAV. Keep 1.x synchronization pointed at upstream `v1`. Rust changes follow
upstream `main`. Do not merge this migration into the production branch until the
acceptance gates below pass.

## Fork-specific parity follow-up

The [fork-only audit](rust-v2-fork-parity-audit-20261107.md) checks the net v1 fork
delta and all capability rows below. It excludes unrelated upstream-only v1
features from implementation scope. The remaining concrete fork follow-ups are
safe legacy eviction #240, legacy administration #249, reconciliation/lifecycle
#250 and failed-import replacement recovery visibility #247. Implemented modules
still need the existing live integration/NAS/performance/restore gates.

Opt-in legacy eviction and access accounting are now implemented locally; see
[eviction qualification](validation/rust-v2-legacy-eviction-20261107.md). Keep its
policy absent during initial in-place migration. No production policy was enabled.

## Latest upstream sync

On 2026-11-07 both remotes were refreshed. The migration branch at
`0c577cc1c8bede5116b8ff5ac8b83b6a9de4d3ad` already contained upstream Rust `main`
through `e3e0507366d9e17abda275018644d3aa92f069f2`; no additional merge was needed.
See [local acceptance](validation/rust-v2-acceptance-0c577cc1-20261107.md) and the
[production migration plan](rust-v2-production-migration-plan.md). Live migration,
integration, NAS and matched v1/v2 performance qualification remain open.

Production storage requirement (2026-11-07): reuse existing cache payloads in
place without copying or removal. An explicit read-only legacy adapter now connects
the original catalogue/payloads to shared playback and warming lookups. See
[in-place cache compatibility](validation/rust-v2-in-place-cache-20261107.md).
Actual NAS handover, rollback and compatible-write qualification remain production
gates; the copy importer is not the required production migration path.

## Status and order

Later increments supersede pending lists in earlier checkpoint sections.

- **Port**: a fork behavior needs an implementation in Rust.
- **Verify**: upstream has a corresponding subsystem; parity needs tests or inspection.
- **Replace**: the old mechanism does not fit 2.x; preserve its user outcome.
- P0 blocks a production trial; P1 preserves the main fork workflows; P2 follows those.
- No row is complete merely because a similarly named Rust module exists.

| ID | Priority | Capability and 1.x specification | Rust starting point / work required | Status |
| --- | --- | --- | --- | --- |
| R01 | P0 | Replace an exactly identified damaged import even when its original grab is absent (`ArrClient`, `RadarrClient`, `HealthCheckService`; fork PR #230) | `crates/arr/src/repair.rs` already deletes/searches without a grab, but unnecessarily waits for an automatic search it never triggered. Skip that wait and retain replacement/error guards. | First port implemented; validation recorded below |
| R02 | P0 | Recover the original download from exact file ID + imported path; reject ambiguity and incomplete history (`ArrDownloadIdResolver`, fork PR #234) | `repair.rs::owner_of_file` and Arr owner backfill exist. Add scoped, fully paginated Radarr history using **`movieIds`**, verify current identity, and carry recovered identity into blocklisting. Do not use a migration job ID as original provenance. Sonarr filtering stays profile-specific. | Implemented; exact scoped/paginated recovery with ambiguity guards |
| R03 | P0 | Protect live/overlapping reads, but bypass recent-read delay for confirmed damage after readers close (`ConcurrentReadTracker`, fork PR #232) | `health::Health::with_in_use` and VFS read handles exist. Prove FUSE/WebDAV/STRM accounting, cancellation cleanup, and no destructive repair while any reader remains. | Implemented persisted reader holds, last-close wakeup and atomic reader/repair admission; regression validation below |
| R04 | P0 | Keep read/warm-triggered damaged-file repairs urgent through provider circuit outages (`HealthCheckService`, `PrefetchRepairHistory`, `eafe1705`) | Persisted urgent health state and bounded inconclusive retry; strict warming failures request the same priority. | Implemented for playback and the shared warming executor, including admitted current/history predictions; all admitted catalogue and playback-backfill policies use the same strict executor |
| R05 | P0 | Resumable imported-content validation and fair forced rechecks (`ImportedContentSweepService`, `IFileRecheckQueue`) | Rust health sampling/rechecks exist. Compare scheduling fairness, known-hole BODY probes, durable cursor, quotas and cancellation. | Durable sampled-content snapshot, cursor, pause/resume, category and run limits implemented; fair maintenance slots and revision-safe fault publication included |
| R06 | P0 | Shared regrab intent/outcome tracking, guarded link removal, detached replacement observation (`Services/Regrab`, `ArrRegrab` API) | Rust health states and `arr::replace` exist; add durable reconciliation for partial/uncertain Arr writes and library-root ownership checks. A non-2xx DELETE may already have removed the link; reconcile before retry. | Durable source-bound journal, detached observation, revision-checked controls and current Arr-root authority implemented; read-only scan-root guards at admission and every mutation checkpoint included |
| R07 | P0 | Bounded PAR2 repair attempts without holding the health worker (`Par2RepairService`, `0a3bba3a`, `42f09136`) | Separate bounded GF16 reconstruction lane with exact original-post proof binding, complete-article assembly, shared-reader/source publication admission and durable intent/outcome recovery. Provider outages defer work without proving permanent damage. | Automatic opt-in worker, durable journal/readback, original-post discovery, shared-reader publication admission and fresh-check completion implemented; runtime qualification below |
| C01 | P0 | Native final-media cache with verified block coverage and source revisions (`Services/NativeCache`, `NativeCachedStream`) | `crates/stream/{cache,disk}.rs` caches decoded articles, not the fork's final-media chunks. Design a shared VFS/stream cache interface before implementing final-media retention. | Implemented shared Rust final-media cache, verified 1.x copy import and explicit read-only in-place 1.x playback/warming reuse; actual NAS acceptance remains open |
| C02 | P0 | Cache folder ownership, mount identity, quota/free-space reserves, watermarks, read-only folders, safe eviction and pinning (`NativeCacheStore`, `NativeFileSystem`) | Rust disk cache has capacity/age eviction; multi-folder placement, file retention and NAS failure behavior need parity. Never reuse legacy cache roots writable during a trial. | Core ownership/quotas/age-pressure eviction/pins, durable retained-copy health protection and bounded block/folder probes implemented; source-bound manifest reconstruction, explicit read-only recovery mode and current-revision verification implemented; live NAS acceptance remains open |
| C03 | P0 | Repair revisions, generation-safe multipart identity, invalidation after replacement/removal (`ContentRevisionTracker`, `RepairRevisionStore`, `MultipartContentIdentity`) | New Rust metadata/blob versions and article cache identities require explicit mapping. Test stale revision exclusion and concurrent eviction/read behavior. | Durable article ledger, admission stamps, decoded-cache namespaces, final-media keys and dependent-reader guards implemented; startup/lineage checks, later cache attachment guards and retained-hash fallback implemented; automatic PAR2 publication is connected through the opt-in durable worker |
| C04 | P1 | Progressive cold reads, bounded first-byte fill, coalesced fills, source window reuse (`NativeCachedStream`, `NativeSourceCacheStream`; `c6a70ba8`, `0c3c129f`) | Rust segment cache already coalesces in-flight reads. Benchmark FUSE/WebDAV seeks before adding buffering; preserve foreground priority and bounded memory/I/O under NAS stalls. | Progressive read-through and parallel block commits implemented; generated cold/seek release benchmarks and fresh-process providerless cache POC passed; live performance qualification remains open |
| C05 | P1 | Native cache browser, verified ranges, eviction history, buffer/I/O diagnostics (`NativeCacheBrowser*`, cache APIs) | Rust cache statistics exist; final-media file/range APIs and catalogue UI depend on C01–C03. Queries must not warm files or scan NAS payloads. | Settings/browser/ranges/pin/evict and eviction-history/folder-diagnostic APIs implemented; probe/history/diagnostic tool UI and physical read/write/removal counters, queued/running work and buffer-slot diagnostics implemented; live storage acceptance remains open |
| P01 | P1 | Durable Smart Prefetch queue, policies, pause/resume/cancel, budgets, bounded lanes (`Services/Prefetch`, `PrefetchJobStore`) | New source-bound durable queue, one strict background lane, manual API/UI and attempted-fill allowance. | Durable wire reservations, scoped hub/collection/library candidates, minimum head/tail and finish-watched policies, adaptive capacity and qualified playback backfills implemented; detailed rate/repair attribution is tracked in P05 |
| P02 | P1 | Plex account/PIN/Home-user authentication, server discovery and mapping (`Services/Plex`, Plex API) | New Rust Plex transport, private connection handles, settings and admin UI. | Implemented; loopback transport/service tests and simulated browser acceptance below; live Plex acceptance pending |
| P03 | P1 | Viewer-scoped next-episode and partially watched predictions; durable snapshots during Plex outages (`PredictionSnapshot*`, `PlexViewerResolver`) | Depends on P02 and the media catalogue. Preserve account identity validation, viewer exclusions and uncertainty states. | Durable complete snapshots and viewer-verified current/history planner implemented; live Plex acceptance pending |
| P04 | P1 | Playback-aware warming priority, viewer lead, own-file warm lane, urgent backfills, cooperative yields (`WarmYieldPolicy`, `WarmConnectionGovernor`, `PrefetchWireBudget`) | Depends on P01 and reader tracking. Cached playback must not consume provider allowance; warming cannot starve an uncached seek. | Foreground yields, own-file priority, dedicated warm workload and conservative per-reader cached-lead protection across handle gaps, bounded adaptive provider capacity and source-bound qualified playback backfills implemented; actual native restart/seek acceptance passed, controlled performance/live acceptance remain separate |
| P05 | P1 | Warming history by source revision/range, fetched bytes, source attribution and repair outcomes (`PrefetchRepairHistory`, `WarmingRateWindow`) | Depends on C01, P01 and R06. Use Rust decimal-byte API/UI conventions. | Durable revision/range-bound terminal history, fill/provider rate windows, dated policy/viewer provenance and separately observed current repair/replacement warming implemented; native and mobile fixture acceptance passed |
| L01 | P1 | Persisted library link index, additional scan roots, video filter, catalogue/grouped browse (`Services/Library`, `LibraryLinkMaps`) | Rust library conversion/reconciliation exists, but not the fork catalogue and grouping. Preserve exact source-to-link mapping and bounded scans. | Persistent configuration-bound link snapshots, additional read-only roots, video grouping and browser implemented; durable directory/entry/link staging, worker fences and atomic complete-generation publication implemented; 51,001-link native restart acceptance passed |
| L02 | P1 | Plex metadata matching and indexed library classification (`PlexLibraryMetadataService`, `LibraryCatalogService`) | Depends on P02/L01. Keep matching separate from authority to delete or regrab a file. | Opt-in complete persistent metadata snapshots, exact mapped/indexed paths and ambiguity-guarded display fallback implemented; live Plex acceptance pending |
| L03 | P1 | File details: source identity, current cache ranges, health history, active regrab, manual regrab (`GetLibraryFileDetails`, file modal) | Compose new Rust admin APIs over L01/C05/R06; do not assume 1.x API compatibility. | Details now include source revision, cache ranges, indexed paths, active regrab and recent grouped reads; Plex classification and source-independent recovery controls included |
| L04 | P1 | Live playback rows grouped across bursts, imported file ID, viewer attribution and cache-served reads (`ActiveReadRegistry`, `ActiveReadsBroadcaster`) | Rust `vfs::session` and traces exist; add only missing correlation/attribution and prove cleanup on reader drop. | Source/client/player burst grouping and exact native-cache served bytes implemented; fresh, scope/revision/time-bound verified Plex source-session correlation implemented with ambiguity preserved; it does not assign an HTTP/FUSE read to a remote viewer |
| M01 | P0 | Import our actual fork data without losing ownership or repair state (`NzbDavMigration`, fork DB additions) | `crates/import` imports C# data, but fork tables/settings/cache layouts need an explicit compatibility report. Inventory unsupported settings and records; fail or report them rather than silently discard. | Private digest-bound SQLite export/verification preserves legacy settings, unknown DB tables and known journals; supported warming policies and read-only library selections now have typed translation with explicit activation review; Read-only, digest-bound PostgreSQL source snapshots and explicit source-timezone import/retry are implemented; uncertain repair requests and removal journals now have immutable typed holds, bounded read-only Arr observation, and original-ID replay protection; guarded local source retirement, retained audit and operational status UI implemented; unchanged generation, fresh replacement proof, automatic-repair policy, reader and cache/publication guards are required; no remote mutation replay |
| M02 | P1 | Legacy NZB catalogue recovery, digest-bound mapped export packages and parallel canary mount (`tools/NzbDavMigration`) | Keep existing tools read-only initially. Decide whether to retain the external tool or add a Rust adapter; preserve package digest, source identity and rollback journal. | Exact original C# article identities, offline generation-fenced member capture and current-revision proof implemented; process-bound physical mount challenges, sampled validation, and separate resumable canary links with conditional offline rollback implemented; completed-job/subtree-bound exact new-ID member proofs and explicit digest-bound map validation implemented; bounded unique-candidate discovery, frozen priority and two-slot canary disposition orchestration implemented; native end-to-end generated canary qualification passed; live integration/cutover remain acceptance gates |
| M03 | P1 | Pipelined batches, separate validation slot, resumable priority schedule and failed-import recovery (`NzbDavMigrationController`, `UsenetMigration`) | New Rust queue/admin contracts need adapters and restart-safe checkpoints. No acknowledgement without terminal proof. | Atomic exact-byte/options-bound migration acceptance and authenticated readback implemented; bounded independent import/validation lanes, exact member discovery, owned canary links, durable terminal dispositions and frozen schedule resume implemented; native end-to-end generated canary qualification passed; live integration/cutover remain acceptance gates |
| M04 | P1 | Resumable external import runner, systemd service and Plex-hub priority ordering (`scripts/operations/plex-import-runner.py`, `plex_hub_priority.py`) | Adapt the operational tools to the new Rust import/admin contracts. Keep existing journals immutable; prove restart, duplicate-submission and priority-snapshot behavior against a separate canary. | Resumable Rust acceptance runner and isolated systemd service/timer templates implemented; complete current Rust prediction snapshots can generate frozen priority schedules, with exact sealed-batch resume; native end-to-end generated canary qualification passed; live integration/cutover remain acceptance gates |
| S01 | P0 | Completed-link removal remains removed across restart (`DatabaseStore`, `3e981833`) | Rust VFS/database cleanup has different links; add protocol + restart tests for `/completed-symlinks` and source ownership. | Implemented with additive SQLite/PostgreSQL migration and WebDAV/FUSE checks |
| S02 | P1 | Bounded cancelled-body cleanup and low-latency multipart reads (`UsenetSharp`, `NativeSourceCacheStream`; `963f3090`, `fe73df69`) | Rust uses Drop/lease guards, ordered pipelines and shared streaming. Verify connection/permit release and cold seek benchmarks; do not mechanically copy timeout values. | Cancellation/permit recovery verified with an explicitly gated stalled provider and next-read regression; controlled live performance remains an acceptance gate |
| S03 | P1 | Validate archived media before import completion; preserve first-volume order (`Queue`, `fe0dd0f2`, `c30fd88e`) | Rust queue/archive analyzer already exists. Port malformed-media and multipart ordering regression scenarios. | Malformed stored RAR/7z media fails before handoff; reversed and obfuscated volume order regressions pass |
| O01 | P0 | Exact source/image provenance and fork version in builds (`scripts/build-prod-image.sh`, `248bd117`) | Rust Docker/release pipeline uses its own embedding. Add fork identity without publishing stable upstream tags; require image/source receipt for trial. | Fork source identity, upstream-update isolation, repository-gated release publication and exact-source trial builder with pinned submodule receipts implemented; canonical local amd64 release image and providerless WebDAV/seek healthcheck POC passed; production and arm64 image acceptance remain separate |

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
passed. `cargo test --workspace --locked` passed: 296 tests, no failures; two ignored tests
(kernel FUSE and local NZB sample tests).
The optional PostgreSQL test returns without exercising PostgreSQL when its URL is
unset, as in this local run. No live Arr/provider acceptance, UI production build,
release image, migration rehearsal or performance qualification is claimed.

## Implemented cache and protocol increment

- R02 uses `movieIds` for Radarr and `episodeId` for Sonarr. It requires both the
  current file ID and exact path, pages to completion within a bounded limit, rejects
  changed totals/repeated pages/ambiguous identities, and never blocklists a migration
  job ID merely because the caller supplied one. Seven new HTTP regressions cover it.
- S01 persists completed-pointer tombstones. Removal affects the pointer subtree,
  not `/content`, `/.ids`, source blobs or download history. HTTP retries are safe;
  category roots and content stay read-only. Migration `0009_completed_links` is
  additive on SQLite and PostgreSQL. **Back up `/config` before upgrading**; the
  migration applies on startup. It does not migrate a 1.x installation by itself.
- `crates/native-cache` owns versioned local SQLite metadata and checksummed final-media
  blocks in chunk files. It verifies folder identity/ownership before writes, retains
  buffer permits through real I/O completion, protects active/pinned generations,
  reserves a playback buffer, and supports read-only folders, quotas, placement and
  bounded age/pressure sweeps. Invalid or incomplete writes never become hits.
- The VFS wrapper serves FUSE, WebDAV and STRM through the same cache. First cold bytes
  do not wait for a full block; only complete blocks without gap fillers are retained.
  Missing/corrupt availability annotations do not change content identity. Message
  IDs, sizes, part ranges/order and crypto do. Fingerprints are lazy and reused.
- Performance settings expose the advanced cache controls with environment locking
  and explicit restart requirements. This is advanced-only: it stays off by default
  and adds nothing to a new installation's critical setup path. Storage/schema/cache
  formats are new; existing 1.x cache roots are not adopted automatically.
- The basic browser reads catalogue metadata, shows coverage and offers guarded
  pin/unpin/evict actions. This is not the full 1.x library or cache-telemetry UI.

### Local acceptance evidence

Use `crates/infinidysk/examples/fork_local.rs` for a disposable, loopback-only fixture.
It creates its own database, mount, generated NNTP corpus and cache under a private
unique temporary directory. No production configuration or provider is used. The
printed receipt gives its URL and paths, without credentials. Create its `stop` file
to stop it. Kernel mount checks need a privileged local test environment with FUSE;
we did not change the host's global `allow_other` policy.

The Chromium run exercised health, a complete generated-file hash on a native cache
miss, cached range/seek hashes, coverage UI, pin/unpin, settings save/restore and
restart indication, mobile overflow, active-read eviction protection (including a
forged size), confirmed eviction and correct source fallback. All ten checks passed,
with no page errors. This is synthetic byte-stream acceptance, not media-decoder or
live Plex/Stash/provider qualification.

A busy default-parallel workspace run hit one existing two-second queue timing
assertion. Its isolated rerun passed. The subsequent full workspace run with two test
threads passed 325 tests, including PostgreSQL; three tests were ignored there. Both
kernel FUSE cases (ordinary and native cache, including cross-UID pointer removal)
passed separately. An additional ownership regression reproduced an unintended lock
file in a rejected nonempty folder; the follow-up fix validates ownership before
creating anything. The focused storage suite passed all 14 cases, and the VFS native
cache suite passed all seven cases, including stored-member range offsets and
encrypted-member plaintext seeks. Workspace Clippy passed after these additions.

### Known remaining gaps

The first cache implementation is not full fork parity: explicit folder probe/scan
and recovery tooling, persistent traffic/eviction browsing,
Plex metadata/attribution, automatic warming/backfill,
and performance envelopes remain pending. The reader-hold scheduling gap is addressed by the increment below. Other recovery
parity still needs its own evidence.

## 1.x native cache compatibility increment

The Rust admin API and Performance settings now provide a resumable **copy import**
from an offline 1.x native cache. This is a format adapter, not shared writable storage.
It reuses existing verified bytes without NNTP reads, but needs separate destination
capacity. The 1.x catalogue, payloads, manifests and repair ledger remain unchanged for
rollback. Pause/resume saves its cursor in the browser. Repeating a batch is idempotent.

Prerequisites and boundaries:

1. Import the C# file records with this branch first. Each converted metadata blob and
   its original source receipt commit atomically. Receipts include the C# raw blob hash
   or stable multipart identity, source dependency hashes, and the Rust content identity.
2. Stop 1.x and expose its original native metadata directory and cache folders to the
   Rust process. The importer takes the existing 1.x owner lock, checks the volume marker
   and registered root identity, and opens payload descendants without following links.
   A live writer, replaced mount, unknown folder or missing repair ledger fails closed.
3. Configure a separate writable Rust cache, then use **Import a 1.x native cache**.
   Source folder IDs must match the original catalogue. The importer reconstructs the
   C# generation from the original repair ledger, checks the catalogue and manifest,
   reads and SHA-256-verifies each complete integrity block, and publishes it under the
   independently verified current Rust key. Both sparse and chunked layouts work.
4. Incomplete, corrupt or stale blocks never become coverage. Original pins are applied
   before a copied block's lease is released. Insufficient destination capacity pauses
   the import. Source files not imported, missing provenance, or changed content are
   reported as rejected; they are not guessed from names or sizes. Earlier trial imports
   without source receipts need a fresh isolated C# import before cache reuse.

Reproduction is in `scripts/fixtures/legacy-native-cache/README.md`. The real C# writer
and full Rust file-import/VFS path passed locally for plain, stored and encrypted-member
metadata, sparse/chunked payloads and partial coverage. No live provider was configured.
This does not certify every historical 1.x schema or a production snapshot.

## Reader and cache-aware repair increment

Reader-held repairs are now persisted. Closing the last overlapping handle advances
only that held repair; a concurrent new handle is checked again before repair. A
startup sweep releases holds left behind by a crash. Unrelated Arr/provider backoffs
are not accelerated. The session callback runs outside the registry lock.

Before destructive recovery of source article damage, a complete native copy is checked
from disk. A valid copy is retained as degraded source health; unavailable storage
postpones replacement. Partial/corrupt copies do not block recovery. A confirmed
container defect still proceeds to repair, since checksums cannot make bad media valid.

Playback-triggered urgency now has its own persisted marker. Inconclusive provider
checks retain that priority, wait five minutes before retrying, and remain eligible
when routine checks are disabled. They do not loop immediately or claim article loss.
The marker clears on a terminal healthy/degraded/ignored result. Warming integration
still depends on the pending Smart Prefetch scheduler.

Migrations `0010_fork_import_and_reader_holds` and `0011_urgent_health` are additive on
SQLite and PostgreSQL. **Back up `/config` before upgrading.** The cache import is an
explicit advanced maintenance action, so it adds no new-install wizard requirement.

The final checkpoint validation is recorded in
[`validation/rust-v2-cache-interop-20261103.md`](validation/rust-v2-cache-interop-20261103.md):
337 workspace tests, PostgreSQL, three real C# interoperability cases, privileged FUSE,
Chromium and storage-interruption/resume checks. This is not full fork feature parity.

## Plex connection and viewer-policy increment

`crates/plex` implements PIN authentication, Plex Home profiles, discovery, protected
server validation, complete bounded library paging, sessions/history and viewer-scoped
metadata and next-episode requests. Viewer access requires account and server proof;
a display name or claimed player ID never selects another account's watched state.
Redirects are disabled, request/response sizes and time are bounded, credentials stay
in sensitive headers, and errors do not echo upstream bodies. Prediction policy keeps
viewers separate, filters specials and duplicate episodes, and uses exact path mapping.
This policy is not yet a durable Smart Prefetch planner.

The admin service keeps login and discovered-token handles private, expiring and bound
to the authenticated administrator. Configuration changes invalidate pending work.
Home selection verifies the actual returned identity and rechecks its parent credential
before saving. Server selection only accepts an advertised URI, verifies machine identity
and library authorization, and rejects stale configuration. Existing mappings survive
credential refresh. Support packs redact current and historical Plex token forms.
The C# settings adapter carries valid account/server lists over, including numeric
legacy account IDs; invalid lists produce an explicit migration note.

The optional Settings > Plex page supports accounts, Home profiles, discovered servers,
connection tests and exact DAV/local mappings. Tokens remain masked. Environment-managed
lists remain locked. **Setup-wizard review:** Plex is optional and default-empty, outside
a new installation's critical path; no wizard change or version bump is needed.
No database schema change is introduced by this increment.

Local evidence: the workspace run passed **360 tests, zero failures, six ignored**.
The PostgreSQL environment was unset in that run; the separate
`postgres_storage_round_trips` run against local PostgreSQL 17 passed. Workspace
formatting, all-target Clippy and the TypeScript/Vite build also passed. Six connection-service cases and sixteen transport/
policy cases cover account ownership, cancellation, identity changes, authorization,
paging, bounds, masking and path/range policy. The Chromium fixture passed eight UI
checks with no page errors: account flow, Home PIN isolation, token-free server selection,
connection test, mappings, masked round trip, mobile width and environment locks.
**The browser responses were simulated.** No real Plex login, cloud acceptance,
production provider access, prefetch runtime or deployment is claimed.

## Durable manual warming increment

`crates/prefetch` stores a bounded, source-revision-bound job ledger in the existing
SQLite/PostgreSQL configuration store under a private versioned key. Generic settings
saves preserve this live ledger. Admission is idempotent for the same intent, revision
and ranges. A single background lane processes aligned complete final-media blocks,
without creating playback sessions. It uses a distinct `warm` provider workload,
a one-article window, strict reads and no fillers, hedges or speculative prewarming.
The cache reserves foreground capacity. A waiting foreground read interrupts a warm
transfer; an idle open playback handle does not block warming.

Pause/resume/cancel, bounded retries, expiry, source supersession and history survive
restart. Restart rechecks retained coverage before advancing progress; evicted bytes
are fetched again. Progress advances only after cache publication. Cancellation of a
stalled source never publishes its partial block. A failed database write cannot
acknowledge a control change. Source errors request urgent health checks and back off;
cache-publication failures back off without calling the source permanently damaged.

Settings > Background warming provides manual full-file admission and job controls.
Settings remain environment-authoritative. **Setup-wizard review:** warming is optional,
off by default and requires the native cache. It remains advanced-only; no wizard
change/version bump is needed. This increment adds no database migration. Keep a backup
of `/config` and retain the 1.x rollback cache before any eventual trial or cutover.

The daily allowance is deliberately named **attempted fill bytes**. Each source block
is charged durably before transfer, including failed/interrupted attempts. Cached
blocks cost no allowance. Article overfetch and NNTP framing are not measured by it;
it is not the fork's full wire-budget governor. Source-byte job counters describe
successfully stored bytes, while provider workload metrics count decoded traffic.
Automatic Plex snapshots/planning, viewer-lead/own-file lanes and library parity remain
open work. No complete Smart Prefetch parity, live provider performance or deployment
is claimed.

Ten focused queue tests passed, covering restart/cache revalidation, idempotence,
controls/allowance, source replacement, urgent repair, database failure, stalled-source
cancellation, foreground contention, stored-member offsets, encrypted plaintext/tail
publication and exact confirmed missing-slice reporting. Inconclusive failures request
priority without recording a false missing slice. The actual Chromium canary passed nine checks
against the Rust API and generated NNTP corpus: paused admission, settings toggle,
submission/idempotence, complete warming, full-file/seek hashes through the shared cache,
reload persistence, cached retry without source charge, and mobile width. No page errors.
This is local generated-byte acceptance, distinct from the simulated Plex browser run.
The initial full run exposed a metrics assertion that still expected three workloads;
the regression now checks warming's requests/bytes separately.

Latest qualification has an explicit limit: the earlier 368-test workspace checkpoint
passed, but the final full run stopped after 314 passes on two unchanged queue timing
cases. Ten warming cases and real PostgreSQL storage passed in that final run; the
original C# interoperability rerun also passed all three cases. Reducing Tokio workers
did not consistently resolve the queue failures. The final source must not be described
as having a green full workspace suite. Details, binary provenance and browser/test
scope are in [Plex and warming validation](validation/rust-v2-plex-warming-20261103.md).


## Durable Plex prediction and automatic admission increment

Rust now refreshes complete, configuration-bound snapshots for current playback,
viewer-scoped next episodes and partially watched movies. The planner uses verified
viewer credentials for metadata and watched state. Unresolved profiles are visible as
ineligible; they never fall back to the owner. Positive watched counts are excluded
from next-episode choices, malformed watched state fails the pass, and mismatched
playing media versions are not guessed from metadata's first Part.

The read-only snapshot keeps the last complete result during a failed/incomplete
refresh and marks it stale after restoration or an outage. Configuration revisions
hash account/server/policy/mapping settings without persisting raw credentials.
Changed configurations hide old results and discard in-flight publication. Results
are bounded to 100 candidates / 2 MB, refresh fetches to 25 seconds, and queue admission
to five seconds. Saved-format errors warn and recover on a fresh complete pass.

Automatic intents carry configuration ownership and an exact source revision. Policy
changes cancel obsolete automatic work while preserving manual requests and explicit
cancellations. Completed intents whose coverage was evicted become pending again;
failed/cancelled intents do not get replaced by endless duplicate retries. Admission
rejects metadata that changed during the preview. Both warming and playback use the
existing shared native cache; no second delivery cache is added.

History reads the newest 1000 events with bounded complete paging. Rewatches use event
identity rather than treating repeated media IDs as duplicate pages. Exact DAV mappings
and recognized local imported pointers are supported. Local pointers are opt-in, verify
known mount/key identity, and have two bounded blocking-I/O slots retained through real
completion. Local lookup uncertainty retains the previous snapshot. No arbitrary local
media is read for warming.

Settings > Background warming includes policy controls and attributed preview rows,
with environment locks and explicit stale/error state. **Setup review:** Plex prediction
is optional and default-off; history and local pointers remain off by default, realtime
is selected when prediction is enabled, and downloading still requires warming/native
cache enablement. This is advanced-only; no setup wizard/version bump is needed. Existing
1.x prefetch settings/journals are not automatically activated by this increment.

Validation and remaining scope are recorded in
[Plex prediction validation](validation/rust-v2-predictions-20261103.md). The latest full
run passed 329 tests, including PostgreSQL, and then stopped on the unchanged stalled-job
queue timing test. It is not a green full-suite result. Library catalogue/hub-source
policies, the full wire/lead/minimum/finish-watched governor, repair integration, broader
migration and controlled live performance acceptance remain open.


## Durable repair and health request increment

Health now persists a source- and Arr-bound regrab intent before dispatching external
mutations. Revision compare-and-swap prevents stale workers from advancing the same
intent. Original file identity survives missing Arr file records and source removal.
A lost blocklist, delete or search response leads to read-only observation, rather
than replay. Deletion must be observed before search; accepted search commands are
matched by fresh command ID and exact arguments. Saved records contain no credentials.
Each checkpoint revalidates the configured Arr and current content revision.

A repair pass is bounded to 25 seconds and waits for automatic search by scheduling a
later pass, without sleeping in the health worker. Uncertain mutations older than one
day require review. Terminal journals retain 30 days of observations. The authenticated
`GET /api/v1/health/regrabs/{id}` reports stage, revision and command ID without exposing
job names or imported paths. There is no manual abandon/reissue action yet.

VFS playback opens and repair admission use the same lock. An admitted reader prevents
mutation until its last handle closes. A repair owner temporarily rejects new opens
(WebDAV 503 with Retry-After; FUSE EAGAIN); stale nodes reload visibility and metadata.
Request generations preserve a same-priority request arriving during health work or
exception backoff. Explicit user requests precede unchecked imports, with persisted
admission time and stable ID ties. This does not implement the resumable full-content
sweep or a full scheduling quota policy.

**Upgrade/setup review:** migrations 0012 and 0013 are additive on SQLite and
PostgreSQL. Back up `/config` before upgrading; migrations apply at startup. No new
configuration or setup-wizard controls are added. Detached journal observation after
source removal, library-root deletion authority, explicit manual recovery controls,
full sweep/PAR2 execution and the other inventory gaps remain open. No production
cutover is implied.

Validation is recorded in [repair recovery validation](validation/rust-v2-regrab-20261103.md).


## Cache inspection and storage-outage increment

Authenticated admin APIs now expose cursor-paged persistent eviction history, read-only
folder diagnostics, and a bounded checksum probe over committed cache blocks. A probe
checks at most 16 blocks and returns a resume cursor. Confirmed corruption removes
coverage; storage uncertainty retains the record and retries the same cursor. The
probe never fetches provider bytes. Folder checks validate the existing owner/mount
identity and free space without creating a marker, payload, or write-test file.
Request deadlines bound API waits while actual blocking I/O retains its permit.

A missing cache folder/ownership marker is now distinct from a missing payload on a
verified folder. This prevents an unavailable mount from clearing good coverage.
Eviction history remains in the local catalogue across restart, capped at 10,000
rows. Cursors exclude newer events without shifting earlier pages. These are admin
API tools; dedicated UI controls, offline catalogue reconstruction, detailed traffic
attribution and PAR2 repair-revision execution remain pending.

No settings or schema changes are added in this increment. Tools operate on the
separate Rust cache; they do not adopt or write into original 1.x cache roots. Setup
wizard behavior is unchanged. Validation is recorded in
[cache tool validation](validation/rust-v2-cache-tools-20261103.md).


## Persistent library catalogue and playback increment

A new authenticated Library page browses video pointers by configured root and top-level
group. Pagination and file details use the persisted index, without walking NAS folders
or opening media payloads. Background scans run only while playback/imports are idle,
one root at a time; least-recently-attempted roots run first. Request generations retain
same-time refreshes admitted during a scan. Configuration/signing changes invalidate
old scopes. A complete result replaces its root atomically; incomplete scans preserve
the last full result with explicit stale/error state.

The first scanner has explicit ceilings: 25 seconds, 150,000 filesystem entries,
50,000 pointers and 16 MB of path/pointer text per root. Refresh cadence is 15 minutes
with a one-minute scheduler tick; failure backoff is five minutes unless a new request
arrives. Directory symlinks are skipped, protected mount/cache/STRM directories are
excluded, and STRM reads are capped after opening. Larger roots require durable paged
staging before this row can be treated as full library parity. Exceeding a ceiling
retains the previous index; it never publishes a partial replacement.

File details compose source revision, current native cache ranges, indexed library
links, saved regrab stage/command and recent playback bursts. Burst grouping preserves
source, delivery, client and signed player identity, with a 30-second idle gap. A
separate bounded lightweight history survives metrics draining, without retaining trace
buffers. Bytes returned directly from native cache are counted per handle and grouped
explicitly. Client text does not prove a Plex viewer; the UI makes no such inference.
Plex metadata matching and viewer correlation remain separate work.

**Upgrade/setup review:** migration 0014 adds catalogue tables/indexes on SQLite and
PostgreSQL; it is additive. Back up `/config` before upgrading. Existing
`imports.library_dirs` controls roots; no setting or wizard field is introduced. A new
install has no roots, so indexing is inactive until folders are configured. The scan
is read-only and never rewrites library links, adopts cache roots or triggers regrabs.

Validation is recorded in [catalogue validation](validation/rust-v2-catalogue-20261104.md).


## Opt-in Plex library classification increment

Library settings now expose default-off metadata matching, with all enabled servers
or an explicit selection (including none). Complete metadata snapshots persist in a
separate database table. Paging, transport deadlines, entry/path/byte bounds and a
single blocking index-build permit prevent unbounded work. Background refresh runs
daily while imports and playback are idle; explicit administrative sync may run
while they are active. A failed/incomplete pass retains the previous complete snapshot.
Configuration changes hide incompatible saved results and reject in-flight publication.

Classification prefers exact configured DAV paths or fresh indexed library paths.
All Plex media Part paths are considered. Filename fallback is display-only and
requires agreement across every candidate's type, title, year and episode identity.
Ambiguity produces a visible warning. Responses cap at 100 matches with total count
and a warning. Classification never fetches media, authorizes deletion/regrab,
admits warming or proves a viewer. Restored snapshots are explicitly stale.

Settings, Library and Explorer expose status and classification without per-detail
Plex or NAS calls. The snapshot stores no credentials or owner watched state. A
configuration digest scopes results; it does not expose the settings it hashes.

**Upgrade/setup review:** migration 0015 is additive on SQLite and PostgreSQL.
Back up `/config` before upgrading. Matching is optional and default-off, with
environment-owned controls locked; it remains advanced-only. There is no new-install
critical path or setup-wizard version change. 1.x legacy setting import is separate
work; this increment does not activate old metadata or prefetch settings.

Validation: [Plex classification checkpoint](validation/rust-v2-classification-20261104.md).


## Cache inspection UI increment

Settings > Performance now exposes folder diagnostics, stored-block verification and
eviction history. Storage checks run on request. File verification processes at most
16 blocks per click, keeps its cursor after storage uncertainty, and resets progress
when the selected source revision changes. Confirmed corruption invalidates coverage;
normal verified source reads refill it. History uses bounded stable cursor pages and
survives reload. No tool triggers source warming, claims catalogue reconstruction or
adopts an unavailable volume.

This UI-only increment changes no schema, settings or setup wizard. Existing additive
migrations still require backing up `/config` before an eventual upgrade. Validation
is in [cache inspection UI validation](validation/rust-v2-cache-ui-20261104.md).


## Legacy fork-state preservation increment

The `fork-export` / `fork-verify` CLI provides a private, receipt-bound evidence
package before migration. It snapshots the legacy SQLite databases with committed WAL
data, retains unknown fork tables/settings, archives known repair/prefetch/library/
health journals, and accepts explicit operational-runner journals. Effective legacy
environment overrides are recorded separately from stored values. Numeric package
filenames, private permissions, size/count bounds, source content-witness checks and a synced
manifest prevent silent partial export or overwriting prior packages. Verification
checks all payloads and the digest recorded outside the package.

This is preservation for later adapters, not job activation or full restore. Source
cache payloads and `/config` blobs remain in their original backup. No uncertain Arr
write or old import acknowledgement is replayed. No schema/settings/setup change is
introduced; existing additive migrations still require a `/config` backup.

Usage and limits: [fork-state export](fork-state-export.md). PostgreSQL-source translation, typed settings adoption,
old journal interpretation and restart-safe operational-runner adapters remain open.


## Detached regrab observation and manual recovery

Replacement journals now have a separate bounded read-only observer and authenticated
revision-checked observe/abandon/resume controls. Their Health-page history survives
source removal, rejects stale controls and never restarts an uncertain mutation from
Prepared. Current Arr root-folder authority guards new mutations; Rust delegates the
exact managed-file deletion to Arr instead of unlinking a local library pointer.

No schema or setting change is added. Validation and the distinction from the C# local
symlink-removal guard are in [regrab control validation](validation/rust-v2-regrab-controls-20261104.md).

## Resumable sampled-content sweep and fair health maintenance

The Health page now starts, pauses and resumes an imported-content sweep. SQLite and
PostgreSQL persist a transactional candidate snapshot separately from its versioned
state and cursor; deleting a candidate does not restart the sweep or lose its count.
A category is a literal single path component. An optional per-run limit pauses the
same snapshot; resume continues it. Controls require the current revision. A new
sweep replaces the previous snapshot and counters only after the running one pauses.

Each file samples first/last and an even 1 percent spread, between 8 and 32 articles
when that many exist. Known holes extend that sample up to 64 BODY reads. Two requests
run at once, with a 25-second deadline for the whole file. Imported byte geometry,
part numbers and plausible ranges are checked before a BODY reaches its caller or
cache sink, with provider fallback. Missing total headers and self-contradictory
obfuscated totals retain the 1.x tolerances. Inconsistent sampled file sizes cannot
produce a healthy result. Partial volumes without proven offsets remain unverified.
This is sampled content verification across the library, not a full-byte checksum.

Provider unavailability, a deadline or malformed metadata is unverified, never proof
of damage. Confirmed missing/corrupt articles enter the ordinary two-check repair
policy, preserving automatic-repair settings, verified cached copies, reader holds
and Arr authority checks. Source identity and metadata CAS prevent findings from
marking a replacement; regular checks now preserve newer playback reports too.
Recent findings retain at most 50 source IDs and outcomes without media titles.

Priority checks and repairs precede pending media analysis. After four priority jobs,
a maintenance slot rotates across the sweep, analysis and routine checks; a continuous
explicit queue cannot exclude those lanes. Foreground streams/imports and new urgent
requests interrupt a sampled file without advancing its cursor. Sweep journal errors
do not prevent other health work. Failed hot files with newer requests rotate their
queue timestamp while preserving the request generation.

No new persistent setting or setup-wizard choice is introduced: this is an advanced
Health operation, outside the critical new-install path. Migration 0016 is additive;
**back up `/config` before upgrading**. Sweep snapshots and progress are stored inside the database for restart continuity.

Validation is recorded in
[the sampled-content receipt](validation/rust-v2-content-sweep-20261104.md).

## Independent import lanes

The sweep qualification exposed an intermittent upstream import stall. Requests
from one import could pipeline behind another import's held BODY. Each import
attempt now has a separate NNTP lane while retaining normal pipelining within its
own lane and the same connection/permit limits. Explicit response gates prove that
another import completes while the first response is still held, rather than
relying on a timed stall. See [the isolation receipt](validation/rust-v2-import-isolation-20261104.md).

## Additional read-only library roots

`imports.scan_dirs` adds explicit read-only roots to the catalogue and Plex matching.
They remain outside conversion and dead-link cleanup, while orphan-source cleanup
reads their pointers to retain referenced sources. Regrab admission and each mutation
checkpoint enforce managed Arr paths; unowned automatic deletion remains disabled
while additional roots exist. Discovery does not grant external mutation authority.
STRM signing keys remain accepted because these folders cannot be rewritten.

The setting defaults to empty, validates path/mapping overlap and honors authoritative
`INFINIDYSK__IMPORTS__SCAN_DIRS` ownership. It is an advanced existing-library option,
so no new-install wizard input is introduced. See [the operator guide](fork-library-scan-roots.md).


## PAR2 reconstruction core (2026-11-04)

R07 now has a Rust GF16 recovery decoder in `crates/par2/src/repair.rs`.
It retains only independent recovery equations, verifies every supplied and
reconstructed slice, enforces missing-slice/memory/scan ceilings and supports
cooperative cancellation. Failed reductions cannot return recovered data.
The default is eight missing slices and 256 MB-scale memory (256 × 1024² bytes
internally); no user setting or setup-wizard change is introduced by this core.

The decoder follows the [PAR 2.0 specification](https://parchive.github.io/doc/Parity%20Volume%20Set%20Specification%20v2.0.html),
including opaque set/file IDs, recoverable input order, GF16 coefficients, full
32-bit recovery exponents and zero-padded final slices. Checksum-valid conflicting
core packets fail closed. Packet checksums alone do not prove reconstructed data:
IFSC validation remains mandatory. The caller must also verify whole-file MD5
before publishing repairs.

Focused validation: nine PAR2 tests pass (one field test, four existing index tests,
four new recovery tests), and PAR2 all-target Clippy passes with warnings denied.
The recovery tests use the existing independent par2cmdline fixture and check
single/multiple/short-slice restoration, whole-file hashes, redundant equations,
incomplete input, cancellation, memory limits, damaged packet hashes, forged
recovery contents and conflicting metadata.

This is a reconstruction foundation, not completed R07 or C03. Provider discovery,
source/range binding, streaming packet collection, whole-file verification,
durable worker scheduling and revision-safe publication remain required. No live
repair, production deployment or cutover was performed.

### Streamed PAR2 packets and final verification

Recovery framing now accepts arbitrary transport chunks, retains at most one
packet window and emits one checksum-valid recovery row at a time. The caller
stops the volume as soon as enough independent equations have been accepted.
Metadata collection has its own ceiling. Scan bytes and hash work are bounded;
EOF resynchronization finds valid packets behind truncated false headers.
Cancellation or budget failure poisons the reader.

`Verification` performs a separate ordered read pass using the recovered slices
and current source slices. Only completion of every recoverable file's whole-file
MD5 check constructs `VerifiedRepair`. Changed input, inconsistent descriptors,
missing final input and poisoned verification cannot produce a publication proof.
This remains a pure sans-I/O component, not a health-worker or cache publication
implementation. Release-wide I/O budgets and source bindings belong to that worker.

Focused validation now passes 13 PAR2 tests and warnings-denied all-target Clippy.
New checks cover one-byte and packet-boundary chunking, early row completion,
truncated false headers, reader cancellation/budgets, whole-file/IFSC disagreement,
changed sources and full 32-bit recovery exponents. Existing par2cmdline fixtures
remain the independent recovery-byte oracle.


### Provider-facing PAR2 execution API

`health::par2::Plan` now executes the three source passes against NNTP: damage
inspection, recovery reduction and final verification. Every source binding must
supply a whole-file imported PAR2 proof and complete trusted decoded article ranges.
Names, partial archive-member spans and estimated article sizes cannot establish
those bindings. The exact descriptor, IFSC, first-16-KB hash, file length and whole
MD5 must agree before provider work starts. Sources follow main-packet input order.

The execution API uses a separate NNTP lane, ordered geometry-checked BODY reads,
a one-article reuse window and blocking workers for hashing/reconstruction. Default
ceilings are eight damaged slices, a 16 GB-scale release, 512 MB-scale recovery
volumes, 100,000 BODY admissions and 256 MB-scale total RAM. Internally the byte
ceilings use powers of two. Source geometry, retained descriptor copies, current
article buffers and packet windows are included in the RAM preflight. Decoded-read
reservations are conservative, are charged before dispatch and survive cancellation;
they are explicitly not a provider wire-byte measurement.

An intact outcome also requires whole-file MD5 proof. Provider outages return a
provider error, not a confirmed missing-slice outcome. Exceeding the missing-slice
policy has its own result, distinct from read/memory allowance exhaustion. An
incomplete, changed or unbound source cannot return a repair proof. The API does
not publish article patches, change cache revisions, schedule work or mutate Arr.
Automatic repair still needs durable discovery, worker/outcome scheduling and C03
publication. R07 remains open until that path is connected and qualified.

Focused validation: five loopback NNTP executor tests pass, alongside 14 pure PAR2
tests and warnings-denied all-target Clippy for health, NNTP and PAR2. Tests cover
an article missing across two slice boundaries, complete reconstructed bytes,
healthy files without recovery reads, early missing/read-budget cutoffs, untrusted
bindings, provider outages, cancellation and all download permits returning.
No real provider, production data or deployed service was used.


The corrected PAR2 checkpoint is qualified by the full workspace run: **479 passed,
0 failed, 6 ignored**, plus the three original C# writer checks and two privileged
kernel FUSE checks run separately. See [PAR2 execution validation](validation/rust-v2-par2-execution-20261104.md)
for exact source, limits, qualification and remaining automatic-repair gaps.

### Durable article repair ledger

C03 now has an independently tested storage primitive in
`native_cache::repair::Store`. The storage primitive can share a local final-media catalogue and works
without final-media payload folders. Application bootstrap uses a separate stable
`/config/repair-v2` directory so moving a cache does not move authoritative repairs. A repair record binds message ID, complete
article geometry, source PAR2 proof, payload hash and a deterministic content revision.
Evicting patch bytes retains the revision and proof; a missing patch cannot silently
become an unversioned provider/cache lookup.

Publication is atomic across a bounded batch. Affected IDs become pending before
mutation. Ambiguous commit or anchor-sync results remain pending until explicit
readback reconciliation. Reconciliation observes the database; it does not replay
patches. A separately fsynced identity/epoch anchor rejects rollback below published
repair state. Only one ledger writer owns the directory. Root/anchor identity,
symlink guards, payload hashes and persisted descriptor/revision consistency are
checked. Hashing and anchor sync run outside async executor threads; owned writer
guards also cover blocking work after caller cancellation.

The storage ceiling is 4 GB-scale patch payloads, 100,000 retained revision records,
256 MB-scale batches, 32 MB-scale articles and bounded ID/record text. Internal byte
ceilings use powers of two. The storage API trusts its publishing caller's whole-file
proof; it does not establish source identity itself. Eight focused ledger tests and
all-target native-cache Clippy pass, including payload eviction/restart, changed
revisions, atomic batch rejection, committed-but-unacknowledged anchor failure,
readback recovery, stale/lost metadata, corrupt payload/geometry, directory swaps,
symlinked anchors and sharing metadata with the existing native cache.

This is not yet attached to playback or automatic repair. Admission-time article
stamps, decoded-cache flights/sinks, final-media keys, dependent-reader changes,
external cache lineage and guarded publication still need integration. C03 remains
open. The new local tables are additive; **back up `/config`, including the native
cache metadata and repair anchor, before upgrading**. No persisted user setting or
setup-wizard branch is introduced by this primitive.

### Repair revisions below delivery

The ledger now implements the NNTP client's verified-article source interface.
BODY and STAT consult repaired articles before provider circuits and missing-article
memoization. Pending or missing repaired payloads return unavailability; they never
silently read the original source. Cached repaired bodies still validate their
immutable article geometry.

Provider admission captures a revision in each request tag. Normal and abandoned
response sinks receive that captured stamp. Memory, disk and in-flight decoded-body
keys include the stamp, with original unmodified IDs preserved at the base revision.
A new repair cannot join an older provider flight. A delayed original body cannot
be stored as a newer repair. Original IDs containing NUL cannot collide with the
internal revision namespace.

Final-media fingerprints include only relevant repaired article IDs/revisions.
Unrelated file keys stay identical. One captured fingerprint covers both the native
cache lease and the outer read guard. Affected existing handles must reopen after
publication, including other items sharing an article. Unrelated readers continue.
The guard checks before and after reads and covers analysis/warming readers too.
Verified repairs clear imported and playback-discovered hole suppression while
retaining monotonic filler counters for cache-fill safety.

The existing 1.x copy endpoint compares the current VFS key with the independently
preserved original source key. A Rust repair changes that key and therefore refuses
to copy original cached bytes into a newer repaired generation. Unmodified keys
remain identical with an empty repair ledger. The original C# full-import fixture
now attaches this repair layer when testing provider-free reads and seeks.

Focused qualification for this integration: **121 tests pass** across NNTP, decoded
cache, native cache and VFS, with the optional original-writer check ignored in that
ordinary run. Workspace all-target Clippy and formatting pass. New integration tests
hold an original NNTP response while a repaired read completes, check cancelled-body
stamps, reopen the versioned disk cache, reject wrong geometry, retire dependent
handles, preserve unrelated reads, and reuse verified final-media data after repair
payload eviction and ledger restart.

Application bootstrap has not attached the ledger yet. Durable external cache
lineage/configuration witnesses must be checked before doing so; a lost metadata
directory must not resurrect an external base-version disk cache. Automatic worker
publication, discovery, outcomes and reader-gated activation are still open. These
storage/stream APIs are not a claim that automatic PAR2 repair is live.


The repair-revision checkpoint now passes **493 workspace tests**, zero failures,
with six ignored; all three original C# writer checks and both privileged FUSE
checks pass separately. The original C# full-import test uses the new repair layer.
See [repair revision validation](validation/rust-v2-repair-revisions-20261104.md)
for exact source and the remaining runtime-bootstrap/publication boundaries.

### Publication lineage witnesses

A repair ledger can now require durable witnesses before activating a committed
batch. The main database (additive migration 0017, SQLite and PostgreSQL), native
cache catalogue and external decoded-cache directory each retain its identity and
monotonic epoch. Late completion cannot replace the ledger identity or reduce the
epoch. The decoded cache also checks lineage before a changed runtime cache path
becomes readable; conflicting paths remain disabled with a diagnostic failure.

A witness error leaves affected articles pending. Readback reconciliation repeats
only the idempotent witness checks and then activates already committed patches;
it never reconstructs or republishes the batch. Marker writes use checksums,
exclusive ownership, atomic replacement and fsync outside async executor threads.
Owned guards cover blocking work even if its caller is cancelled.

Focused validation passes ten ledger tests, sixteen decoded-cache unit tests and
seven main-database tests, including actual PostgreSQL. New cases prove post-commit
witness failure/readback, older/different ledger refusal, persistent external-cache
markers and runtime path rejection without rewriting the conflicting marker.
Warnings-denied all-target Clippy passes for these three crates. Application startup
and the background worker still need to install and use these witnesses.

No setup-wizard setting is introduced by these internal contracts. Migration 0017
is additive: **back up `/config` and any external cache metadata before upgrading**.


### Authoritative repair bootstrap and health publication barrier

Startup now opens existing repair data from the stable config-directory path before
attaching the article source to NNTP. Existing repairs remain authoritative when new
attempts are disabled. A fresh default installation does not create a repair ledger.
The automatic PAR2 setting and worker are not enabled by this increment.

Main-database, native-cache and decoded-cache witnesses are read before any binding
write. A missing bound ledger, a different identity or a witness newer than the
available ledger stops startup with a recovery message. Startup does not manufacture
an empty replacement or poison fresh main metadata before detecting an external
cache mismatch. Publication updates every active witness before activation.

Health and publication share a mutation barrier. A repair publication queued before
an Arr/deletion step must be observed before that step proceeds. Pending publication
defers replacement. When repairs cover all recorded holes, health requests a fresh
urgent check; coverage alone never marks the file healthy. Partial coverage does not
suppress the existing replacement policy. Changes during health I/O defer stale
verdict publication. Existing repairs receive this readback even with new automatic
repair attempts disabled.

Five application startup tests and four health integration tests cover these paths.
The full run passes **504 tests**, zero failures, six ignored; all three original
C# writer checks and both privileged FUSE checks pass separately. See the
[repair bootstrap qualification](validation/rust-v2-repair-bootstrap-20261104.md).
Automatic discovery, full-article patch assembly, durable attempt outcomes and the
separate PAR2 worker are still open. No API/UI setting or setup-wizard branch is
introduced here. Back up `/config` and external cache metadata before upgrading.

Startup also seals the intentional absence of repair state. A cache folder attached
later cannot introduce a foreign repair-history marker when no authoritative ledger
is active; that disk tier stays disabled and its marker is left intact. Six startup
tests and workspace all-target Clippy pass for this follow-up. This closes the case
where an external cache was unavailable during startup and became available later.

### Complete verified repair articles

`Plan::execute_articles` now converts the whole-file verification result into
complete articles for durable publication. It includes every article intersecting
recovered slices. Healthy portions of mixed articles are read again and checked
against the original IFSC before copying. Changed, missing or unbound bytes cannot
produce a partial publication batch. Reconstructed bytes retain exact whole-file
identity and decoded geometry; unknown yEnc part/total fields are not invented.

Assembly shares the original execution request/read allowance. Its default decoded
allowance includes a bounded additional patch batch; this is not a wire-byte budget.
Before article allocation, it bounds the batch at 1,024 articles and 256 MB-scale
payloads and accounts for retained recovered slices, patch buffers and per-article
metadata against the remaining RAM allowance. Cancellation is checked during reads,
checksums and copying. No storage mutation occurs inside this executor.

Nine provider-facing executor tests and warnings-denied health all-target Clippy
pass. New cases round-trip all complete articles through the real repair store and
provider-free NNTP reads, exhaust the original request allowance at assembly, and
switch providers after whole-file verification to prove that changed healthy bytes
in a mixed article are rejected. Automatic discovery, durable scheduling/outcomes,
source/reader-gated publication and the separate worker remain open.

### Payload retention and conclusive repair readback

Known absent repair payloads can now use provider fallback while retaining their
repair revision. Every candidate BODY must match both immutable geometry and the
retained SHA-256 before delivery. Progressive delivery is disabled for this path.
Validation precedes normal and abandoned cache sinks; cancelled wrong original
responses cannot enter the repaired namespace. STAT also reads and proves the BODY.
Provider absence is conclusive only when every configured provider answered;
no providers and repair-directory I/O uncertainty remain unavailable.

A durable per-item health receipt binds the exact source metadata identity and its
repaired article revisions. Only a conclusive check of still-current metadata and
request generation can record it. Rechecking a retained tombstone therefore cannot
keep resetting a confirmed missing source into the same readback, including after
restart. Changed repair revisions require a fresh independent check. Native-copy
retention and the existing Arr/reader guards still apply before replacement.

Health also holds the publication barrier from generation comparison through verdict
and receipt persistence. A queued writer cannot activate in that gap. A deterministic
concurrency test proves that the old verdict records only the old revision.

Focused qualification: 45 tests pass across database SQLite/PostgreSQL, health and
decoded-cache unit/integration targets; workspace all-target Clippy passes. Main
migration 0018 is additive and its receipts are deleted with their item. Back up the
config directory and external cache metadata before upgrading. No setting, API
shape or setup-wizard control is introduced. Automatic PAR2 discovery, durable
attempt scheduling/outcomes and the worker still remain open.

### Original-post discovery for automatic PAR2 planning

`health::par2::discover` now probes original NZB article IDs and constructs complete
source/volume ranges for the verified executor. Names are only probe hints. Damaged
sources require an imported PAR2 proof and an exact contiguous ID span. Additional
healthy sources require matching length, whole MD5 and first-16-KB MD5. All descriptor
fields and IFSC entries must match the imported anchors. Conflicting same-set core
metadata is rejected; display-name differences do not establish source identity.

A single missing or conclusively corrupt article can receive an exact range from
validated neighboring boundaries (or the proven whole-file extent for one article).
Consecutive unknown ranges are not divided or guessed. Nonuniform part sizes are
preserved. Every recoverable file must bind before a plan is returned. Corrupt copies
remain inconclusive if any provider was skipped or failed to connect.

Discovery uses its own NNTP lane and CPU workers, a two-minute timeout, cancellation,
source-geometry/RAM preflight, 100,000 logical request ceiling and bounded decoded
read reservations. Unknown responses reserve the 32 MB-scale decoder ceiling until
a complete response proves the actual length. Uncertain/discarded responses retain
the reservation. Remaining request/read allowances pass into execution; this is not
wire-byte accounting. Current input ceilings are 512 original posted files and 64
imported proof anchors; the preflight reserves 112 MB-scale fixed parsing/response
headroom plus input/geometry storage. Limits and unbound ranges are uncertain, not
proof that Arr should replace a source.

Focused qualification: sixteen executor/discovery tests, twelve NNTP unit tests and
thirteen health unit tests pass. Health/NNTP all-target warnings-denied Clippy passes.
New cases exercise partial imported ID spans, complete verified repair, variable
article lengths, consecutive unknown ranges, forged anchors, low-memory rejection
before provider work, request limits, outages, cancellation/permit return and actual
bad-CRC recovery. Durable attempts, scheduling, source/reader-gated publication and
the application worker/settings are still open. No production provider was used.

### Admission for guarded repair publication

VFS sessions now bind their exact cached source metadata before constructing a
reader. A repair can reserve the target item's mutation slot and every affected
article ID atomically. Readers in other items sharing those IDs defer publication;
unknown opens defer it until metadata is bound. New overlapping opens cannot return
bytes while the reservation is held. Existing and new unrelated readers continue.
Source references are weak so completed playback history does not pin metadata.
Dropping the reservation uses the existing last-close wakeup for the target.

`Store::publish_checked` evaluates a bounded read-only source check after acquiring
the publication barrier, before a write transaction or pending revision. A source
changed while publication was queued is refused without changing generation/epoch.
Cancelling validation releases the writer without an uncertain commit to replay.
The publisher can calculate a patch's deterministic expected revision before writing,
so the durable worker can record an intent and reconcile acceptance after restart.

Focused native-cache/VFS admission tests and all-target warnings-denied Clippy pass.
New cases cover shared sources, unknown opens, unrelated reads, target mutation
exclusion, wakeups, metadata retention, queued-source changes and cancelled checks.
The worker still needs to acquire these guards, write its intent and invoke the
checked publisher. This increment does not activate automatic reconstruction.

### Durable PAR2 attempt admission and transitions

The main database now journals attempts by exact item/source digest. Concurrent
admission returns one accepted identity and does not overwrite its payload. Pending
work is capped at 128 and retained history at 1,024; full capacity never prevents
readback of an existing accepted intent. Count/eviction/admission serialize through
a singleton row on SQLite and PostgreSQL. Only terminal history can be evicted.

Updates compare the original identity, source and revision. Recovery changes running
work to deferred while leaving publishing intents intact for acceptance readback.
Publishing readback precedes new queued work, including urgent work. Rows deliberately
outlive item deletion so replacement cannot erase an uncertain publication intent.
Payloads are bounded to 1 MB-scale and states are validated. The application worker
must write publishing before mutation and recover only while holding the exclusive
repair ledger writer; the journal itself performs no provider or Arr operations.

Seven database tests pass with actual SQLite/PostgreSQL, including concurrent
idempotent admission, competing requests for the last capacity slot, stale/wrong-source
CAS refusal, interrupted-running recovery, preserved publishing payloads, priority
and capacity release. Database all-target warnings-denied Clippy passes. Migration
0019 is additive: back up the config directory and external cache metadata before
upgrading. No setting or setup-wizard control is introduced by the journal.

### Settled repair-ledger snapshots and bounded metadata

A controlled WAL transaction reproduced a readback race: reconciliation could
activate the preceding snapshot while an earlier dispatched writer was unresolved.
Startup and reconciliation now acquire a SQLite write barrier and read epoch,
quotas and records through one transaction/connection. They release SQL before
waiting for the health mutation barrier, preserving shared-catalogue lock order.
Fourteen repair-store tests pass, including the reproduction after the fix and a
matching startup case; native-cache all-target warnings-denied Clippy passes.

Repair workflows also have bounded compressed-blob reads at the database and bounded
NZB/metadata expansion in the core decoder. Output bytes and zstd history windows
are limited; an 8 MB-scale history floor accepts the existing streaming writer's
frames even for tiny payloads. Existing unbounded APIs and stored formats remain
unchanged. Sixteen core tests and seven SQLite/PostgreSQL database tests pass;
workspace all-target Clippy passed for the bounded-input increment before subsequent
worker wiring began. These are resource guards, not an automatic-repair activation.

### Automatic local PAR2 worker and runtime controls

R07 now connects original-post discovery, bounded reconstruction and article publication
through a separate durable background worker. Health admits or observes attempts without
performing provider work inline. Playback and warming urgency survives provider outages.
Target readers defer work before provider reads; publication also reserves shared article
IDs so another imported file cannot keep reading a superseded article generation. Last
reader close wakes deferred attempts. Four urgent attempts admit one due routine attempt.

The worker records a source-bound publication manifest before any authoritative write.
Uncertain publication is read back before new discovery. Startup recovers running attempts
but retains publishing intents. Successful publication and an urgent fresh health check
commit in the same main-database transaction. A journal acknowledgement never itself
marks the file healthy. A resource or binding limit falls through to the existing
confirmed-damage replacement policy during its cooldown; provider uncertainty retains
pending work and never proves permanent damage.

Advanced Health settings expose opt-in repair, missing slices, working-buffer allowance
and failure cooldown. Fresh installations default to disabled. Enabling on an installation
without a repair ledger requires restart; existing repaired articles remain authoritative
when new attempts are disabled. The stable `repair-v2` directory must be backed up with
the database and restored together. The status API and Health page show bounded attempt
history without exposing provider article IDs or verification manifests.

Setup-wizard impact: advanced-only. These controls are not required to configure providers,
a mount or an Arr client. The wizard version and required strategy bundles do not change.
Environment-owned values retain the existing server-enforced settings authority. The
working-buffer setting uses decimal MB and does not claim to cap all process memory.
The additive main-database journal migration requires a config backup before upgrading.

Eight SQLite/loopback integration tests passed, covering actual GF16 repair,
shared cached playback and seeks after providers are removed, last-reader admission,
provider outages, a no-patch healthy outcome, and uncertain-publication recovery without
provider replay. Source-change admission, cooldown/no-replay, foreground yielding and authenticated runtime/API checks also pass. The workspace checkpoint passed 543 tests with zero failures, and all-target warnings-denied Clippy passed. Browser acceptance against the rebuilt embedded UI passed settings persistence, first-enable restart notice, empty repair history and 390px mobile layout.
This is local generated-provider evidence, not a production deployment or live provider
acceptance result. The remaining Smart Prefetch, catalogue and migration inventory rows
are still tracked separately.

### Last-reader maintenance admission and cache protection

A deterministic regression reproduced a last-reader/maintenance deadlock: the admitted
health step waited for a second write permit behind a pending exclusive maintenance
pause, while the pause waited for that same health step. Internal last-reader reconciliation
now reuses the caller's admission. The external close callback still obtains its own permit.
The regression fails before the change and passes afterward.

Complete native copies retained for a damaged source now gain durable health protection
before storage verification. A storage outage or canceled verification keeps protection;
conclusively incomplete/corrupt coverage releases automatic protection. Manual pins are
never released by maintenance. Source removal or revision change releases only automatic
protection before ordinary eviction. Explicit user unpinning permits eviction, as the cache
browser explains. Pin changes hold a generation lease through the durable update so eviction
cannot race between admission and SQLite persistence. Two new storage tests cover restart,
manual-pin preservation, partial coverage, mount loss and checksum corruption. No new
setting or setup-wizard branch is introduced.

Final qualification of automatic repair and retained-copy protection: **546 workspace tests passed, zero failed**, full warnings-denied Clippy, three original C# writer tests and two actual kernel FUSE tests. The rebuilt embedded UI passed Chromium settings/history/mobile checks. See [automatic repair validation](validation/rust-v2-automatic-repair-20261104.md).

## Smart Prefetch policy checkpoint

Source `0c66162e` adds durable response-byte reservations, immutable warming outcomes,
finish-watched and minimum head/tail policies, verified-viewer hub/collection/library
candidates, and decoded-memory reuse without provider credits. Full workspace tests
passed (562/0/6); the rebuilt native example passed actual timed WebDAV playback,
eviction/refill and Chromium settings/history checks. See the
[policy validation receipt](validation/rust-v2-prefetch-policies-20261104.md).
These controls are advanced-only. Back up `/config` before startup migrations.
The complete fork port, migration rehearsal and deployment gates remain open.

## Typed fork policy migration

Supported Smart Prefetch values and read-only library selections now pass through the
C# importer. Native storage paths stay inactive, warming requires explicit review,
and unsupported identities/fields produce migration notes. See the
[policy migration receipt](validation/rust-v2-fork-policy-migration-20261104.md).
This does not activate legacy journals or complete PostgreSQL/operational migration.

## Native cache recovery checkpoint

Source `c237a355` adds recovery manifests, explicit owned-volume reconstruction,
current-source verification, bounded resumable pages, conservative repair uncertainty
and budget-free warming holds. A real native restart/catalogue-loss rehearsal
recovered 8 MB through the UI and served full reads/cross-block seeks with no providers.
After a normal restart, warming completed entirely from the recovered cache.
The full suite passed 578/0/6, plus three original C# writer and two kernel-FUSE cases.
See the [cache recovery receipt](validation/rust-v2-cache-recovery-20261104.md).
The full fork port and production cutover remain unfinished.

## Large-library staging checkpoint

Source `7a1ff9d5` removes the former one-pass 50,000-link ceiling. Private generations
resume across restarts and leave complete visible snapshots intact until atomic
publication. The full suite passed 581/0/6 and both SQL backends passed the staging
contract. A native process/Chromium rehearsal indexed 51,001 read-only links across
restart; observed public counts were only 10 and 51,001, with every symlink unchanged.
See the [large-library receipt](validation/rust-v2-large-library-staging-20261104.md).
Back up `/config` before the additive staging migration. Full fork parity remains open.


## Adaptive warming and playback backfill checkpoint

Source `0880d5b7` adds the bounded spare-capacity window and source-bound playback
backfill workflow. It also fixes a shared response-credit refill lock stall exposed
by concurrent warm articles. **591 workspace tests passed, zero failed, six ignored**;
formatting, full warnings-denied Clippy, API/UI builds passed. Three original C# writer
interop and two kernel-FUSE cases passed separately. Actual native playback left two
partial blocks, automatic backfill stored their 8,388,608 missing bytes, and a native
restart retained the job and served the tested range/seek with providers removed.
See the [capacity receipt](validation/rust-v2-warming-capacity-20261104.md) and
[backfill receipt](validation/rust-v2-playback-backfill-20261104.md). Full migration,
verified Plex correlation, detailed rate/repair attribution and release qualification
remain open.


## Plex source-session correlation checkpoint

Source `aaff9828` adds verified selected Plex sessions to exact-source playback/file
rows. It preserves multiple candidate players/viewers, rejects stale/restored or
mismatched source evidence, and keeps absent session paths unresolved. The full
suite passed 593/0/6 with SQL backends, Clippy and API/UI builds. Actual native APIs
prove absent verification stays unknown; a separately labelled UI response fixture
qualifies multi-viewer/mobile presentation. See the
[correlation receipt](validation/rust-v2-playback-correlation-20261104.md).
Warming rate/repair follow-up, historical backfill context and migration remain open.

## Warming visibility qualification

Source `641c2308` adds bounded fill and provider-response rates, stall visibility,
durable policy/viewer provenance at admission and current repair follow-up beside
immutable warming outcomes. [Validation and limits](validation/rust-v2-warming-visibility-20261104.md).
The full port still requires migration workflows and the trial/release gates above.

## PostgreSQL source adapter

The PostgreSQL source adapter preserves public table types/raw rows and imports local
times using the old container timezone. Interrupted/tampered snapshots are refused.
See [source migration limits and validation](validation/rust-v2-postgres-source-20261104.md).

## Migration admission and cache contention

Migration receipts survive uncertain HTTP responses, process restart and history pruning.
[Admission validation](validation/rust-v2-migration-admission-20261104.md) keeps acceptance
separate from member proof. [Cache writer contention](validation/rust-v2-cache-recency-20261104.md)
records a reproduced and fixed verified-read fallback defect found during validation.

## Exact legacy member evidence

Original C# identity vectors now match Rust. Offline member capture is fenced against
current import metadata and never fetches provider data. Identity proof remains distinct
from payload validation. [Validation](validation/rust-v2-legacy-member-identity-20261104.md).

Legacy uncertain-action migration and its qualification are recorded in [legacy repair state](validation/rust-v2-legacy-repair-state-20261105.md). Canary validation is recorded in [mount and canary validation](validation/rust-v2-mount-canary-20261104.md).

New-ID recovery member correlation is recorded in [reimport member proof](validation/rust-v2-reimport-member-proof-20261105.md).

Operational pipeline design and qualification are recorded in [migration pipeline](validation/rust-v2-migration-pipeline-20261105.md).

Native queue/NNTP/FUSE pipeline execution and fresh-process providerless cache reuse are recorded in [native migration canary](validation/rust-v2-native-migration-canary-20261105.md).
