# Smart Prefetch and Native Cache Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` to execute task by
> task. Use `superpowers:subagent-driven-development` only if parallel agent work
> is explicitly selected. Checkboxes describe future work, not completed features.

**Goal:** Add tested native final-file caching and smart predictive warming to an
isolated InfiniDysk testing branch without replacing existing capabilities.

**Architecture:** Select exactly one cache mode: Off, Segment, or Native. Native
uses a verified multi-folder file-range store below the existing shared-stream
pump and above the existing format/NNTP stack. A bounded queue accepts manual and
configurable Plex prediction intents; existing provider admission, discovery, repair,
bandwidth, and article-memory owners remain authoritative.

**Tech Stack:** .NET 10, xUnit, existing UsenetSharp/SharpCompress references,
React Router/TypeScript/Vitest, versioned filesystem manifests, existing Docker CI.

## Scope and execution rules

Read the [design](../specs/2026-09-20-smart-prefetch-native-cache-design.md) first.
Also read the [50 TB architecture comparison](../specs/2026-09-20-50tb-cache-architecture-comparison.md):
the confirmed workload is HDD/NAS and mostly whole movies/episodes. Prefer one
sparse data file per media generation, a verified range map and local disk-backed
catalogue; benchmark container alternatives on representative NAS storage.
This is a comprehensive staged implementation plan, not a complete source patch.
The type names and signatures below are proposed contracts. Reconcile them with
then-current source before implementation; do not claim snippets have compiled.

Deliver the core cache and manual warmer before external playback integrations.
Multi-folder management is required in the first usable native-cache UI. Plex
login, server discovery, hubs/collections, source previews and configurable
smart policies are required for feature completion, even though users may leave
them disabled. These user requirements supersede the initial coexistence design.
Keep feature flags off until the applicable gate passes. No donor database/cache
conversion is required; do not couple this effort to the separate migration plan.
An existing migration canary may be used later only with independently verified
test data and ownership. No source or production cleanup belongs to these tasks.

Repository instructions assign broad automated checks to PR CI. Add meaningful
regressions to that suite; run focused local tests only for diagnosis/development
of a failing case. Do not repeatedly run the full CI suite locally. Run local
timing benchmarks and manual playback/rclone checks that CI cannot establish.
Each task ends with a scoped commit; implementation PR title uses `feat` or `fix`
as appropriate. This planning-only change uses `chore(docs)`.

## Task 0 — Pin baseline and create the testing branch

**Files:** existing `AGENTS.md`, `CONTRIBUTING.md`, design capability table;
new `docs/operations/native-cache-canary.md` (runbook, not generated results).

- [ ] Read current local guidance, inspect status/remotes/worktrees, fetch fork
  main, and compare changes in all capability-table owners since `a809fa2e`.
- [ ] Create `feat/smart-prefetch-native-cache` from current fork main in an
  ignored isolated worktree. Never switch or commit someone else's dirty work.
- [ ] Record target SHA, donor SHA `f6c14875`, runtime, native library build,
  corpus hashes, current configuration, and resource limits in the test report.
  Check donor license/provenance before copying any actual code; preserve notices.
- [ ] Capture cache-off and existing segment-cache baseline reports using
  existing benchmark harnesses. Use fake/loopback NNTP for automated scenarios.
- [ ] Commit runbook: `chore(docs): define native cache integration canary`.

```bash
git fetch origin main
git worktree add .worktrees/smart-prefetch-native-cache \
  -b feat/smart-prefetch-native-cache origin/main
# Within that worktree, follow CONTRIBUTING.md to build host natives.
dotnet run --project backend.Benchmarks -c Release -- \
  --nntp-whole-path-report --set quick --json /tmp/native-cache-baseline.json
```

Expected baseline: deterministic hashes, BODY counts, callback and resource
cleanup fields are recorded. These runs establish a comparison, not a new
feature pass. Reuse CI reports when available instead of repeating them locally.

## Task 1 — Extract one content-opening path, preserving behavior

**Create:** `backend/Services/Streaming/DavContentStreamFactory.cs`,
`backend/Services/Streaming/ContentReadPurpose.cs`,
`backend/Services/Streaming/ContentReadScope.cs`.
**Modify:** `backend/WebDav/DatabaseStoreNzbFile.cs`,
`DatabaseStoreRarFile.cs`, `DatabaseStoreMultipartFile.cs`,
`backend/WebDav/Base/BaseStoreStreamFile.cs`, `backend/Program.cs`.
**Tests:** new `tests/NzbWebDAV.Tests/Services/DavContentStreamFactoryTests.cs`;
existing `Streams/DavMultipartFileStreamTests.cs`,
`Streams/SharedStreamEntryTests.cs`, and `Clients/Usenet/StreamingTimeoutTests.cs`.

- [ ] Characterize direct NZB, legacy RAR, lazy multipart, and encrypted reads
  using deterministic content; assert exact bytes, offsets, disposal and retries.
- [ ] Extract only opening/scope responsibilities. Keep format streams and all
  existing constructor options, trusted maps, proofs, and known-hole propagation.
- [ ] Introduce explicit foreground/prefetch/verification purposes. Map prefetch
  to existing low/background priority; retain foreground timeouts and permit rules.
- [ ] Verify detached ownership survives the originating HTTP response and that
  background tokens never classify as foreground. Verify failed construction
  releases scope/permits, and normal disposal does so exactly once.
- [ ] Commit: `chore(streaming): centralize content opening and read scope ownership`.

Contract sketch, with all returned streams owned by the caller:

```csharp
public enum ContentReadPurpose { Foreground, Prefetch, Verification }
// DavContentStreamFactory.OpenAsync(Guid itemId, ContentReadPurpose purpose,
//     CancellationToken ct) returns a Stream plus IAsyncDisposable scope ownership.
// The caller disposes the stream before the scope; both paths are idempotent.
```

## Task 2 — Define content revisions and exact-range fidelity

**Create:** `backend/Services/NativeCache/NativeContentIdentity.cs`,
`ContentRevisionProvider.cs`, `backend/Streams/RangeFidelity.cs`.
**Modify:** target gap/verification streams `NzbFileStream.cs`,
`UnbufferedMultiSegmentStream.cs`, `ContainerAwareFillStream.cs`,
`DavMultipartFileStream.cs`, `AesDecoderStream.cs`, `Par2VerifiedFileStream.cs`;
`backend/Services/Repair/RepairPatchStore.cs`,
`backend/Services/Repair/DavNzbFileBlobUpdater.cs`,
`backend/Services/LazyRarResolver.cs` and `SharedStreamRegistry.cs` invalidation hooks.
**Tests:** new `Streams/RangeFidelityTests.cs`,
`Services/NativeCache/ContentRevisionTests.cs` under `tests/NzbWebDAV.Tests/`.

- [ ] Enumerate every target path that synthesizes bytes or changes a persisted
  article map, archive map, decryption interpretation, size or repaired article.
  Record its invalidation hook in tests; no unsupported path gets native writes.
- [ ] Implement immutable identity/revision calculation. Hash metadata once per
  version, not article IDs on every read. Add a persistent conservative fence for
  repairs whose dependent items cannot be identified; rotate it before mutation.
- [ ] Propagate exact range evidence through format and shared-stream wrappers.
  Unknown fidelity defaults to noncacheable. Preserve existing streaming tolerance.
- [ ] Assert same-ID/same-size remap, repaired patch, lazy resolution and restart
  invalidate stale entries; fence an in-flight writer from committing old bytes.
- [ ] Assert real zero-filled payload is accepted when verified, while synthesized
  zeros and transport-stream null packets are rejected. Reject false source maps
  even when downloaded length matches. Partial verified ranges remain usable.
- [ ] Commit: `feat(streaming): expose content revision and cacheable range evidence`.

```csharp
public readonly record struct NativeContentIdentity(
    Guid ItemId, Guid? BlobId, long FileSize, string Revision);
public readonly record struct CacheRange(long Start, long EndExclusive);
public enum RangeFidelity { Unknown, SourceVerified, RepairedVerified, Synthetic }
// Cache admission requires SourceVerified or RepairedVerified for the entire
// requested CacheRange and the same NativeContentIdentity at publication.
```

## Task 3 — Implement the native store and crash-safe publication

**Create under `backend/Services/NativeCache/`:** `NativeCacheManifest.cs`,
`NativeRangeStore.cs`, `NativeCacheCatalog.cs`, `NativeCacheLeaseRegistry.cs`,
`NativeCacheWriter.cs`, `NativeCacheRecoveryService.cs`, `NativeCacheFolder.cs`,
`NativeCacheFolderRegistry.cs`, `NativeCacheRootPolicy.cs`,
`NativeCacheCatalogStore.cs`, `NativeCacheRangeJournal.cs`.
**Tests under `tests/NzbWebDAV.Tests/Services/NativeCache/`:**
`NativeRangeStoreTests.cs`, `NativeCachePublicationTests.cs`,
`NativeCacheLeaseTests.cs`, `NativeCacheRecoveryTests.cs`, `RootPolicyTests.cs`.

- [ ] Write generated-byte tests for overlapping/adjacent/disjoint extents,
  short final extent, nonzero starts, integer overflow and out-of-file ranges.
- [ ] Implement one internal data file per media generation with immutable
  published spans, exact checksummed range records, layout version and bounded
  metadata. Sparse holes never count as valid data. Mark completion without
  copying the full media file. Compare 64/256 MiB containers in Task 10.
- [ ] Implement local auxiliary catalogue schema/versioning, indexes, bounded
  page/hot caches and batched updates. Keep it out of NAS roots/main DB. Derive
  authoritative coverage from manifest/journal evidence, not catalogue counters.
- [ ] Add byte/job reservations before buffer rental. Own each buffer until
  persistence or cancellation finishes; skip caching when capacity is exhausted.
- [ ] Implement bounded catalog recovery, owner lock and revision validation.
  Persist dirty-entry tracking before gap writes; replay only bounded dirty tails
  on ordinary restart. Inject failure before/after data flush, journal commit,
  manifest rename, catalogue update and restart. Lost catalogues rebuild separately.
- [ ] Verify missing/malformed metadata never means fully cached; corrupt extent
  affects only its coverage. No double-return/use-after-return of pooled buffers.
- [ ] Persist a folder list with stable IDs, names, paths, quotas, age limits,
  priorities, enabled/read-only flags and Local/NFS/SMB/Unknown types. Implement
  deterministic placement and per-folder free-space/reservation accounting.
- [ ] Add private file permissions, root ownership marker, symlink/path containment
  checks, and distinct roots. Reject overlapping aliases and a second writer.
  Read-only roots accept compatible immutable snapshots with no filesystem writes.
- [ ] Test two writable roots, priority/quota fallthrough, unavailable roots,
  read-only hits, existing generation ownership and restart reconciliation.
  Test owner exclusion/rename semantics for NFS/SMB before allowing writable use.
- [ ] Commit: `feat(cache): persist verified native file ranges atomically`.

Test vectors (implement as table-driven xUnit cases against the store):

| Published input | Expected readable coverage |
| --- | --- |
| `[0,8)`, `[8,16)` same revision | `[0,16)` with exact source bytes |
| `[0,8)`, `[12,16)` | Two ranges; read across gap must fetch `[8,12)` |
| `[0,16)` data write, no range commit | No hit after restart |
| Valid manifest, truncated extent | Affected extent is a miss |
| Old revision, same length | No hit |
| Cancel/evict during write | Either committed valid generation or a miss, never partial hit |

## Task 4 — Integrate read-through and mutually exclusive cache modes

**Create:** `backend/Streams/NativeFileCacheStream.cs`,
`backend/Services/NativeCache/NativeRangeFetchCoordinator.cs`,
`backend/Config/CacheModeResolver.cs`.
**Modify:** `DavContentStreamFactory.cs`, `BaseStoreStreamFile.cs`,
`backend/Clients/Usenet/UsenetStreamingClient.cs`,
`backend/Services/SegmentCacheCleanupService.cs`, `backend/Program.cs`,
`backend/WebDav/Base/GetAndHeadHandlerPatch.cs`,
`backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs` only where
needed to pass purpose/range/identity; preserve both entry points.
**Tests:** new `Streams/NativeFileCacheStreamTests.cs`,
`Services/NativeCache/NativeRangeFetchCoordinatorTests.cs`,
`Api/NativeCacheReadContractTests.cs`, `Config/CacheModeResolverTests.cs`;
extend `SegmentCacheNntpClientTests.cs` and `DeleteCacheDirTests.cs`.

- [ ] Wrap the final decoded stream once, below shared readers, with lazy fallback.
  Cache hit cannot invoke archive resolution, decryption, or provider acquisition.
- [ ] Implement exact-gap reads and seek/reset semantics. Recheck revision on
  each new lease; stale shared entries are drained via existing lifecycle rules.
- [ ] Implement `cache.mode=off|segment|native` and legacy alias resolution from
  the design. Construct only the selected cache services/client chain. Native
  cannot read old segment entries, enqueue segment writes or load its catalog.
  Keep repaired-article precedence and verification-purpose native bypass.
- [ ] Require restart for mode changes; expose active/pending mode, drain old
  workers on shutdown and validate provider-client reconfiguration. On native
  failures or unknown fidelity, use source streaming without enabling Segment.
- [ ] Protect retained inactive files: explicit Native/Off transitions do not
  trigger legacy disabled-segment cleanup. Cover legacy absent-mode behavior,
  contradictory environment pins, atomic settings saves and restart transitions.
- [ ] Coalesce range fills with foreground-aware handoff and independent waiter
  cancellation. Do not wait for a background job while holding its required lock.
- [ ] Test shared on/off with each of Off/Segment/Native, mixed hit/miss batches, HEAD/416, `/view`
  and DAV GET, disconnect, end-of-file, seek behind/ahead, and dual readers.
- [ ] Assert fully cached bytes yield zero BODY calls; a cold eligible Native read
  has one native publication and no segment access, even with old segment files
  present. Segment mode must perform no native access. Verify bypass
  actually exercises source verification and remains correctly attributed.
- [ ] Commit: `feat(cache): serve native ranges through existing streaming paths`.

## Task 5 — Add quotas, eviction and diagnostics before warming

**Create:** `backend/Services/NativeCache/NativeCacheEvictionService.cs`,
`NativeCacheStatistics.cs`, `NativeCacheIntegrityService.cs`.
**Modify:** `backend/Services/Diagnostics/MemoryComponentSnapshot.cs`,
`backend/Services/Observability/PrometheusMetrics.cs`, `PrometheusMetricsCollector.cs`,
`backend/Services/SupportPack/SupportPackService.cs` and redaction.
**Tests:** new `Services/NativeCache/NativeCacheEvictionTests.cs`,
`NativeCacheStatisticsTests.cs`, `NativeCacheIntegrityTests.cs`.

- [ ] Enforce quota including reserved/in-progress physical bytes and the free-disk
  floor. Batch access stats; never update a database row on each buffer read.
- [ ] Track real allocated blocks, range journals, retired generations and scratch;
  coordinate roots sharing a filesystem. Use indexed per-folder high/low-watermark
  eviction with bounded batches, never sort/load the full 50 TB catalogue.
- [ ] Implement retained/pinned versus evictable media and expose admission-full
  status when protected entries consume quota. Whole cold generations are the
  initial eviction unit; range-level hole punching is not needed for this workload.
- [ ] Evict unleased cold entries with one sweep per root and bounded candidates.
  Protect pending writes/readers; stop when all candidates are protected.
- [ ] Implement manual retirement with bounded busy response and cancellation.
  Optional integrity scans read owned extents only and have I/O/rate budgets.
- [ ] Expose native hit/miss/committed/invalidated/evicted bytes, writer reservations,
  orphan recovery, duplicate fetches, format failures and metadata counts per
  active folder and globally. Apply per-folder age/size eviction. Keep
  article-cache metrics intact; avoid item IDs/titles as Prometheus labels.
- [ ] Fault-test disk-full, inaccessible root, slow writes, live eviction, shutdown,
  and malformed manifests; preserve foreground reads with rate-limited warnings.
- [ ] Commit: `feat(cache): bound native storage and expose cache diagnostics`.

## Task 6 — Build one bounded manual prefetch scheduler

**Create under `backend/Services/Prefetch/`:** `PrefetchIntent.cs`,
`PrefetchQueue.cs`, `PrefetchAdmissionPolicy.cs`, `PrefetchRangeExecutor.cs`,
`PrefetchHostedService.cs`, `PrefetchStatistics.cs`, `ManualPrefetchJournal.cs`.
**Modify:** `backend/Program.cs`, existing workload/metrics integration as needed.
**Tests under `tests/NzbWebDAV.Tests/Services/Prefetch/`:**
`PrefetchQueueTests.cs`, `PrefetchAdmissionTests.cs`, `PrefetchExecutorTests.cs`.

- [ ] Build a bounded deduplicated priority queue with fake clock, TTL, retry limit,
  cancellation and stable ordering. Coalesce ranges for the same revision.
- [ ] Execute 64 MiB windows through the shared content factory and native writer.
  Request only missing ranges. Never issue HTTP requests to the local DAV server
  or open a second NNTP client pool.
- [ ] Use existing low/background admission, bandwidth and article budgets.
  Introduce no direct semaphore bypass or copied Scheduler v2.
- [ ] Enforce worker/queue/daily-byte budgets and pressure pause. Recheck pressure
  between windows; promote/handoff work on foreground demand.
- [ ] Persist manual jobs only; restart paused, expire obsolete identities, and
  derive progress from committed range coverage. Recompute speculative jobs later.
- [ ] Test queue overflow, expiration, cancellation before/during fetch, failed
  providers, reserved-byte cleanup, bounded retry and foreground contention.
- [ ] Commit: `feat(prefetch): add bounded manual native cache warming`.

```csharp
public enum PrefetchSource { Manual, VerifiedPlayback, Prediction, Collection, Minimum }
public sealed record PrefetchIntent(
    NativeContentIdentity Content, CacheRange Range, PrefetchSource Source,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Attempt);
// Queue key: content revision + normalized range, not title or filename.
// Completion: committed union covers Range; received bytes alone cannot complete it.
```

## Task 7 — Add settings, authenticated operations and setup review

**Create:** `backend/Api/Controllers/GetNativeCache/`,
`WarmNativeCache/`, `EvictNativeCache/`, `GetPrefetchStatus/`,
`SetPrefetchState/` with one action/controller following target patterns;
`frontend/app/routes/settings/streaming/native-cache.tsx` and tests;
folder actions `GetNativeCacheFolders/`, `SaveNativeCacheFolder/`,
`RemoveNativeCacheFolder/`, `ProbeNativeCacheFolder/`, `ScanNativeCacheFolder/`,
`ClearNativeCacheFolder/` under `backend/Api/Controllers/`.
**Modify:** `backend/Config/ConfigKeys.cs`, `ConfigManager.cs`,
`frontend/app/routes/settings/validation.ts`,
`frontend/app/routes/settings/streaming/streaming.tsx`,
`frontend/app/clients/backend-client.server.ts`,
`backend/Services/SetupWizardService.cs`, `frontend/app/routes/setup/`,
`contracts/openapi/admin-v1.json`, `docs/configuration/streaming.md`.
**Tests:** new `Config/NativeCacheConfigTests.cs`, `Api/NativeCacheAdminTests.cs`;
extend `Api/SetupWizardControllerTests.cs` and colocated setup/streaming tests.

- [ ] Add the cache-mode selector and initial settings/defaults from the design with server-side
  validation, environment ownership, managed controls and restart indicators.
- [ ] Build a donor-equivalent multi-folder editor: add/edit/remove, name/path,
  size/age/priority/type, enabled/read-only, test, scan, explicit clear and live
  usage/availability. Confirm removal retains contents unless explicitly cleared.
  Reject overlapping roots and removal of busy entries with actionable feedback.
- [ ] Expose 64-bit quotas, free-space reserve, eviction watermarks, retention/pin
  policy and verified-local metadata path. Show useful cached versus allocated
  bytes; distinguish inactive retained stores and media protected from eviction.
- [ ] Test folder CRUD with two roots, read-only/environment-owned fields, reload,
  pending-restart state, validation errors, partial outages and per-folder quotas.
  Do not present the first native UI as complete until this works end to end.
- [ ] Add authenticated paginated cache/job views and bounded warm/pause/cancel/
  resume/evict operations. Validate DAV IDs/ranges; no arbitrary path selection.
- [ ] Wire UI through the existing server-side client and auth proxy. Distinguish
  bytes ready from bytes scheduled; explain rejected/deferred/busy operations.
- [ ] Keep Native/Smart Prefetch advanced/off; enforce cache exclusivity in UI,
  backend, startup and wizard Review. Warn only about external rclone overlap.
  Test completion allowlists, server strategy bundles, environment-pinned
  conflicts and unchanged symlink recommendations. No wizard-version bump here.
- [ ] Add settings docs and cache-at-rest note. Assign a `since` pill only when
  the introducing release is known. Refresh contracts using the existing export
  script and generated TypeScript workflow described in CONTRIBUTING.md.
- [ ] Commit: `feat(ui): manage native cache and warming from streaming settings`.

## Task 8 — Deliver configurable Plex login, discovery and playback

**Create under `backend/Services/Prefetch/`:** `PlaybackSignal.cs`,
`PlaybackSignalRegistry.cs`, `ImportedItemResolver.cs`,
`NextEpisodePrefetchPolicy.cs`, `PlexPlaybackSource.cs`.
**Create under `backend/Services/Plex/`:** `PlexAccountService.cs`,
`PlexServerDiscoveryService.cs`, `PlexApiClient.cs`, `PlexCatalogueService.cs`,
`PlexHistorySource.cs`, `PlexSourceSelection.cs`.
**Create admin action folders:** `StartPlexLogin/`, `GetPlexLoginStatus/`,
`CancelPlexLogin/`, `DisconnectPlexAccount/`, `GetPlexServers/`,
`TestPlexServer/`, `SavePlexServers/`, `GetPlexLibraries/`, `GetPlexUsers/`,
`GetPlexSources/`, `PreviewPlexSource/`, `SavePlexSources/`, `SyncPlexSources/`
under `backend/Api/Controllers/` using target action conventions.
**Create frontend:** `frontend/app/routes/settings/plex/plex.tsx`,
`frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`,
`plex-sources.tsx` in the same Smart Prefetch folder, and colocated tests.
**Modify:** ConfigKeys/config validation, existing Settings UI and secret resolver
for optional runtime activation; register services through `Program.cs`, wire
settings navigation/loaders/actions and `backend-client.server.ts`, regenerate
`contracts/openapi/admin-v1.json` and frontend types.
**Tests:** `Services/Prefetch/PlaybackSignalTests.cs`, `ImportedItemResolverTests.cs`,
`NextEpisodePrefetchTests.cs`, `PlaybackSourceContractTests.cs`;
new `Api/PlexLoginTests.cs`, `Services/Plex/PlexDiscoveryTests.cs`,
`PlexCatalogueTests.cs`, `PlexConfigurationTests.cs` and frontend login/source tests.

- [ ] Review donor `PlexPlaybackDavItemResolver`, selected Media/Part parsing,
  `RecentPlaybackPriorityTracker`, and `EpisodePredictionService` against fixtures.
  Port pure decisions with provenance, not full service dependency graphs.
- [ ] Inventory donor `PlexAuthController`, integrations login UI,
  `PlexMovieSourceService`, `PlexTvSourceService`, `PlexMovieCategories.tsx`,
  `PlexTvCategories.tsx` and Smart Prefetch controllers/settings. Trace every
  required parity-table row in the design to an endpoint, UI and contract test.
- [ ] Implement browser PIN login with admin-session ownership, stable installation
  client ID, expiry/cancel/retry, reconnect/disconnect and manual server/token setup.
  Store tokens server-side; expose opaque handles, never token-bearing URLs or logs.
- [ ] Discover owned/shared servers, list/test local/remote/relay connection
  candidates, save multiple servers and exact path mappings. Test invalid tokens,
  unreachable candidates, permission changes, server rename and multiple admins.
- [ ] Fetch/select libraries and history users; discover global and library-specific
  movie/TV hubs and collections. Support manual refresh, source previews, enabled
  flags, per-source limits and TV excluded shows. Persist IDs rather than labels.
- [ ] Share bounded transport/pagination/parsing across movies and TV; coalesce
  refresh and isolate per-server failures. Confine source keys and authenticated
  redirects to the selected server; private LAN servers remain supported.
- [ ] Add bounded authenticated polling using managed HTTP clients, fresh verified
  signals and exact item mapping. Ignore ambiguous title/path/quality matches.
- [ ] Predict next imported episode, next-season start and resume window. Use
  cached metadata/indexed queries; avoid whole-library scans each poll.
- [ ] Maintain separate verified and raw-read expiries. Confirm scans, previews,
  health reads, and long raw GETs cannot become verified playback or refresh it.
- [ ] Configure history/realtime switches and intervals, trigger toggles, lookback,
  minimum episodes, queue-ahead, episodes-per-show, movie/TV enablement and source
  limits in UI and backend. Test persistence, defaults and environment management.
- [ ] Allow only explicit account/server configuration; mask tokens in settings/test
  responses/logs/support packs. Test timeouts, API failure, stale sessions,
  selected media among multiple versions, season gaps and duplicate identities.
- [ ] Commit independently: `feat(plex): connect accounts and select library sources`,
  then `feat(prefetch): warm upcoming imported episodes from verified playback`.

## Task 9 — Complete required configurable smart policy parity

**Create under `backend/Services/Prefetch/`:** `HistoryPrefetchPolicy.cs`,
`CollectionPrefetchPolicy.cs`, `MinimumRangePrefetchPolicy.cs`.
**Modify:** corresponding config/UI, existing Watchtower event boundary if used;
new `Services/Prefetch/PredictionPolicyTests.cs`, `PlexPolicyParityTests.cs`.

- [ ] Re-express donor movie/TV/watch-history scores as pure, bounded policies.
  Keep history minimal with retention limits; raw watch history stays out of logs.
- [ ] Feed selected Plex movie/TV hubs and collections into the same queue. Reuse
  Watchtower sources where applicable without making Plex catalogue capabilities
  depend on Watchtower being enabled. Do not duplicate release/indexer resolution.
- [ ] Add opt-in head/tail minimum warming and explicit full-file policy with byte
  previews. Test confidence thresholds, cooldown, unavailability and budget denial.
- [ ] Expose warming concurrency and per-item/connection caps as constraints on
  existing NNTP admission, plus byte budgets and warm-local-file eligibility.
  Require Native mode and an enabled writable folder; native failure never
  enables Segment. Disabling a trigger cancels queued work owned only by it.
- [ ] Complete configurable prediction/source views, manual/bulk warm, reorder,
  failed jobs, cancel/retry, manual history/source sync and bounded history.
  Keep raw read-activity signals distinct from verified Plex sessions.
- [ ] Test full login -> server/library/source selection -> preview -> saved
  policy -> warm -> native hit flow, with two folders and an unavailable root.
  Cover movies, TV, exclusions, permission loss, expiry and restart persistence.
- [ ] Commit policies with scoped `feat(prefetch)` messages. The complete feature
  claim requires all required Plex parity rows; manual-only remains intermediate.

## Task 10 — Measure, harden and complete the test-branch canary

**Create:** `backend.Benchmarks/NativeCacheBenchmarks.cs`,
`backend.Benchmarks/NativeCacheCatalogBenchmarks.cs`,
`tests/NzbWebDAV.Tests/Streams/NativeCacheRegressionMatrixTests.cs`,
`tests/NzbWebDAV.Tests/Services/NativeCache/NativeCacheCatalogScaleTests.cs`.
**Modify:** existing `backend.Benchmarks/RepeatableStreamingReport.cs` or
`NntpWholePathReport.cs` for deterministic integration scenarios;
`.github/workflows/ci.yml` only if existing path classification misses coverage;
`docs/operations/native-cache-canary.md` with actual evidence links.

- [ ] Run automated matrix in PR CI: direct/multipart/encrypted, each cache mode,
  both shared-stream modes, repair/remap, mixed batches, cancel/restart/fault/evict.
  Assert final hashes, callbacks, provider request counts and zero leaked permits.
- [ ] Run focused local diagnostics only when a test fails. Preserve donor-derived
  regressions for range sidecars/publication, stale mapping, buffer ownership,
  verified-source expiry and exact progress; translate to target APIs.
- [ ] Measure cold/partial/full cache, overlapping readers, random seeks and
  background contention on the same host. Record NNTP bytes, CPU, disk writes,
  p50/p95 startup/seek, throughput, physical buffers, working set, and usefulness
  of speculative bytes consumed within 24 hours. No performance claims without data.
- [ ] Compare Off, Segment, Native with one folder, Native with multiple folders,
  and existing rclone baseline on a disposable mount. Never benchmark an active
  Segment+Native combination as a supported mode. Verify inactive-cache nonaccess
  and record retained inactive disk bytes separately.
- [ ] Add large-catalogue models from the 50 TB analysis, testing indexed point
  lookup, pagination, startup, quota eviction, journal recovery and bounded RAM.
  Generate rows with bounded tools, not 50 TB of fake payload or millions of files
  in the shared workspace. Report synthetic metadata separately from real I/O.
- [ ] On dedicated representative HDD/NAS storage, compare sparse per-media files
  with 64/256 MiB containers using actually written data, both whole-media and
  fragmented ranges. Test cross-root outages, single-filesystem free-space sharing,
  pinned-full admission and local catalogue loss/checkpoint contention. A sparse
  apparent-size test cannot establish 50 TB real-capacity readiness.
- [ ] Require the design's 5% disabled-path/10% startup/95% throughput gates,
  no stale or synthesized cached bytes, and no resource growth over a two-hour
  churn run. Run a 24-hour opt-in prediction soak before calling smart policies ready.
- [ ] Refactor only measured hot spots: range-index lookups, allocations/copies,
  coalesced metadata writes, or repeated metadata queries. Keep ownership and
  data-integrity tests; do not trade validation for headline throughput.
- [ ] Commit measured changes with `perf(cache)`/`perf(prefetch)` and regression
  fixes separately. Keep raw environment-sensitive benchmark output as artifacts,
  not revised CI baselines that hide a regression.

Existing benchmark invocation, with the new class added by this task:

```bash
dotnet run --project backend.Benchmarks -c Release -- \
  --filter '*NativeCacheBenchmarks*'
```

Expected: fixed-fixture bytes and counters are correct, timing samples are saved,
and comparisons state uncertainty. A successful build is not a canary pass.

## Testing image and deployment runbook

1. Push the implementation branch to the fork and open a **draft** PR to main
   using `gh pr create --repo johoja12/infinidysk`. Read back its exact head SHA
   and URL. Never create upstream records or merge without an explicit request.
2. After applicable CI gates pass, build the exact SHA with the existing
   `.github/workflows/build-custom-image.yml`; record image digest. Example:

   ```bash
   gh workflow run build-custom-image.yml --repo johoja12/infinidysk \
     -f ref=feat/smart-prefetch-native-cache \
     -f image_tag=test-smart-prefetch-native-cache
   ```

   Resolve and record the branch SHA before dispatch and verify the workflow
   checkout afterward; use the immutable SHA as `ref` for repeatability. This is
   a future execution command, not a workflow dispatched by the planning task.
3. Use a new disposable `CONFIG_PATH`, native root, and verified-unused private
   ports. Import a small known test corpus. Keep credentials outside tracked files.
   Do not mount production state writable, share donor cache roots, register this
   instance as an Arr client, or point production rclone/Plex at it.
4. Verify health, image digest, feature settings and counters before each phase.
   Test through the authenticated frontend path and direct backend as distinct
   checks. Use disposable rclone mounts for range scrubbing and real encrypted
   playback; a localhost NNTP benchmark alone cannot prove those paths.
5. Select Native with two configured folders; test manual warming, read-through,
   Plex login/server discovery/library selection/source preview, next episode,
   then history/hubs/collections. Exercise per-source settings and a root outage.
   Collect per-state queue samples and committed coverage;
   do not infer a stall from an unchanged total alone.
6. On byte mismatch, stale content, unbounded growth or foreground regression,
   pause prefetch, disable native cache and restart only the test instance as
   required. Restore recorded baseline digest/config. Retain failing cache and
   reports for inspection; cleanup is a separate explicitly scoped action.
7. Report source commit, CI, image build, test-instance activation, and observed
   playback separately. Main remains unchanged until an exact merge is requested.

## Milestones, dependencies and exit checklist

| Milestone | Dependencies | Reviewable outcome | Rough effort |
| --- | --- | --- | --- |
| A: behavior-preserving extraction | 0–1 | One stream factory and explicit workload ownership | 2–3 days |
| B: integrity and native read-through | 2–5 after A | Exclusive modes, multi-folder verified cache and safe eviction | 1–2 weeks |
| C: manual warm/UI canary | 6–7 after B | Cache selector, full folder editor and bounded queue | 4–6 days |
| D: required Plex and smart policy parity | 8–9 after C | Login/discovery/hubs/previews and configurable history/realtime producers | 2–3 weeks |
| E: optimization and rollout evidence | 10, after each slice | Reproducible comparisons and isolated canary | 3–5 days plus soak |

Allow additional measured effort for the local persistent catalogue and NAS
capacity exercises; the estimates above are not evidence of 50 TB readiness.

Estimates assume one developer familiar with the target. Fidelity/invalidation and
external playback mapping are the largest uncertainties; budgets are estimates,
not promises. Each milestone can be reviewed without merging the experiment.

- [ ] Capability table revalidated; no duplicated active read-ahead, pools,
  discovery/repair service, dashboard or settings authority.
- [ ] All required donor behaviors have a shipped/tested stage or an explicitly
  documented optional limitation; manual-only is not labeled complete smart prefetch.
- [ ] Segment and Native are mutually exclusive through UI/API/env/startup and
  mode transitions; neither may read or write the other's inactive store.
- [ ] Multi-folder UI and placement/quotas/read-only/outage tests pass.
- [ ] Whole-media storage layout and local persistent catalogue pass the scale
  gates; metadata simulation and actual occupied-capacity evidence are separate.
- [ ] Every required Plex parity row has backend, UI and test evidence, including
  login, discovery, hubs/collections, source previews and persisted configuration.
- [ ] Actual bytes, provenance, callback/lease lifetimes and revision fencing pass.
- [ ] Flags off preserve existing behavior; new storage and RAM remain bounded.
- [ ] Setup/env-managed/secret/restart behavior and contracts are covered.
- [ ] PR CI and manual target-host canary evidence recorded separately.
- [ ] No production changes, mutable release tags, accidental cache migration,
  main push, or unauthorized merge.
- [ ] Commit all task-owned work, verify remote head, and leave the isolated
  workspace clean. Reset to main only where it cannot disturb another agent's
  checkout; never stash or commit unrelated work to satisfy a handoff rule.
