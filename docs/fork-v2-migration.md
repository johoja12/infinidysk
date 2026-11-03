# Fork feature migration to Rust 2.x

This is the implementation backlog for `feat/rust-v2-fork-migration`. It starts from
upstream Rust `main` at `b16e071ed60815d34cdb9a84ccc60695bceb7fea` (2.0.0-beta.1 line).
The behavior specification is fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
The inventory was derived from the fork's non-merge commits and file differences
against upstream `v1` at `4a630deb6e599f3eadbe071a92417a93d74dc81f` on 2026-11-03.
It groups related changes into capabilities; it is not a promise of complete parity.

**Current checkpoint:** the missing-grab fallback, exact original-download recovery,
completed-pointer removal, and a first shared final-media cache implementation are in
Rust. Cache settings and basic browsing/pinning/eviction are wired into the UI. The
full fork port is **not complete**. Plex connections and viewer-safe transport are now implemented; Smart Prefetch,
library catalogue parity, PAR2 execution, legacy migration adapters and several repair
scheduler behaviors are still pending. A durable manual warming lane is implemented;
automatic Plex planning and the full warming governor are not yet connected. The draft must not be treated as a cutover.

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
| R02 | P0 | Recover the original download from exact file ID + imported path; reject ambiguity and incomplete history (`ArrDownloadIdResolver`, fork PR #234) | `repair.rs::owner_of_file` and Arr owner backfill exist. Add scoped, fully paginated Radarr history using **`movieIds`**, verify current identity, and carry recovered identity into blocklisting. Do not use a migration job ID as original provenance. Sonarr filtering stays profile-specific. | Implemented; exact scoped/paginated recovery with ambiguity guards |
| R03 | P0 | Protect live/overlapping reads, but bypass recent-read delay for confirmed damage after readers close (`ConcurrentReadTracker`, fork PR #232) | `health::Health::with_in_use` and VFS read handles exist. Prove FUSE/WebDAV/STRM accounting, cancellation cleanup, and no destructive repair while any reader remains. | Implemented reader-held repair persistence and last-close wakeup; regression validation below |
| R04 | P0 | Keep read/warm-triggered damaged-file repairs urgent through provider circuit outages (`HealthCheckService`, `PrefetchRepairHistory`, `eafe1705`) | Persisted urgent health state and bounded inconclusive retry; strict warming failures request the same priority. | Implemented for read and manual warming; automatic prediction integration pending |
| R05 | P0 | Resumable imported-content validation and fair forced rechecks (`ImportedContentSweepService`, `IFileRecheckQueue`) | Rust health sampling/rechecks exist. Compare scheduling fairness, known-hole BODY probes, durable cursor, quotas and cancellation. | Verify / port gaps |
| R06 | P0 | Shared regrab intent/outcome tracking, guarded link removal, detached replacement observation (`Services/Regrab`, `ArrRegrab` API) | Rust health states and `arr::replace` exist; add durable reconciliation for partial/uncertain Arr writes and library-root ownership checks. A non-2xx DELETE may already have removed the link; reconcile before retry. | Port |
| R07 | P0 | Bounded PAR2 repair attempts without holding the health worker (`Par2RepairService`, `0a3bba3a`, `42f09136`) | `crates/par2` currently parses indexes and verifies slices; this baseline has no equivalent recovery executor in health. Port trusted range/slice identity, bounded repair budgets, durable outcomes and separate worker scheduling before declaring repair parity. | Port |
| C01 | P0 | Native final-media cache with verified block coverage and source revisions (`Services/NativeCache`, `NativeCachedStream`) | `crates/stream/{cache,disk}.rs` caches decoded articles, not the fork's final-media chunks. Design a shared VFS/stream cache interface before implementing final-media retention. | First implementation in Rust; verified 1.x copy import implemented; broader fork data import remains M01 |
| C02 | P0 | Cache folder ownership, mount identity, quota/free-space reserves, watermarks, read-only folders, safe eviction and pinning (`NativeCacheStore`, `NativeFileSystem`) | Rust disk cache has capacity/age eviction; multi-folder placement, file retention and NAS failure behavior need parity. Never reuse legacy cache roots writable during a trial. | Core ownership/quotas/age-pressure eviction/pins implemented; recovery/probe tools pending |
| C03 | P0 | Repair revisions, generation-safe multipart identity, invalidation after replacement/removal (`ContentRevisionTracker`, `RepairRevisionStore`, `MultipartContentIdentity`) | New Rust metadata/blob versions and article cache identities require explicit mapping. Test stale revision exclusion and concurrent eviction/read behavior. | Source identity and revision cleanup implemented; PAR2 repair-revision integration pending |
| C04 | P1 | Progressive cold reads, bounded first-byte fill, coalesced fills, source window reuse (`NativeCachedStream`, `NativeSourceCacheStream`; `c6a70ba8`, `0c3c129f`) | Rust segment cache already coalesces in-flight reads. Benchmark FUSE/WebDAV seeks before adding buffering; preserve foreground priority and bounded memory/I/O under NAS stalls. | Progressive read-through and parallel block commits implemented; controlled performance qualification pending |
| C05 | P1 | Native cache browser, verified ranges, eviction history, buffer/I/O diagnostics (`NativeCacheBrowser*`, cache APIs) | Rust cache statistics exist; final-media file/range APIs and catalogue UI depend on C01–C03. Queries must not warm files or scan NAS payloads. | Basic settings/browser/ranges/pin/evict implemented; history/traffic diagnostics pending |
| P01 | P1 | Durable Smart Prefetch queue, policies, pause/resume/cancel, budgets, bounded lanes (`Services/Prefetch`, `PrefetchJobStore`) | New source-bound durable queue, one strict background lane, manual API/UI and attempted-fill allowance. | Manual warming implemented; automatic planner, wire budget and full fork policy parity pending |
| P02 | P1 | Plex account/PIN/Home-user authentication, server discovery and mapping (`Services/Plex`, Plex API) | New Rust Plex transport, private connection handles, settings and admin UI. | Implemented; loopback transport/service tests and simulated browser acceptance below; live Plex acceptance pending |
| P03 | P1 | Viewer-scoped next-episode and partially watched predictions; durable snapshots during Plex outages (`PredictionSnapshot*`, `PlexViewerResolver`) | Depends on P02 and the media catalogue. Preserve account identity validation, viewer exclusions and uncertainty states. | Transport, viewer proof and prediction policy implemented; durable snapshots/planner pending |
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
| S01 | P0 | Completed-link removal remains removed across restart (`DatabaseStore`, `3e981833`) | Rust VFS/database cleanup has different links; add protocol + restart tests for `/completed-symlinks` and source ownership. | Implemented with additive SQLite/PostgreSQL migration and WebDAV/FUSE checks |
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
