# Smart prefetch and native file cache integration design

Status: proposed design for a testing branch; no feature implementation or deployment.
Date: 2026-09-20.

## Objective and evidence boundary

Bring NzbDav's persistent final-file cache and predictive warming into InfiniDysk,
refactoring the imported responsibilities into bounded components. Preserve
InfiniDysk's existing streaming, caching, repair, discovery, and setup behavior.
Optimization means measured improvements in startup, seeking, provider traffic,
CPU, and memory; it is not established by moving code into smaller files.

Source snapshots inspected:

- Donor: `/opt/projects/nzbdav`, commit
  `f6c14875fb008906c244527156fd75463d4aa0b0`, branch
  `jira-NZBDAV-1279-source-map-repair-lane`. This is the inspected checkout,
  not a claim about donor main or its deployed image. Untracked files were excluded.
- Target: `johoja12/infinidysk`, `origin/main` at
  `a809fa2e120ef029dd0875973b12be0147615411`.
- Planning branch: `chore/prefetch-cache-integration-plan`, based directly on
  target main. It does not depend on the separate library migration PR.
- Proposed implementation/testing branch: `feat/smart-prefetch-native-cache`.
  Create it from then-current fork main when implementing; recheck this inventory
  against its exact SHA before changing code.

Findings below are source observations, not runtime or performance measurements.
Do not import the donor's database, cache directory, configuration, migrations,
connection scheduler, or unrelated repair changes as part of this feature.

## Existing-capability map and ownership

All target paths in this table are relative to the repository root.

| Responsibility | Existing target owner/evidence | Integration decision |
| --- | --- | --- |
| Persistent decoded articles keyed by Message-ID | `backend/Clients/Usenet/SegmentCacheNntpClient.cs` | Keep public behavior, layout, settings, and mixed-hit batch overlay. This is not a final-file cache. |
| Bounded asynchronous article writes | `backend/Clients/Usenet/SegmentCacheWriteBehind.cs` | Retain; reuse reservation/ownership patterns, not its article-specific queue for file extents. |
| Article-cache metrics and cleanup | `SegmentCacheStatistics.cs`, `backend/Services/SegmentCacheCleanupService.cs` | Retain. Native file storage gets a distinct root and format marker. Existing disabled-cache cleanup must never traverse it. |
| Per-stream adaptive prefetch and range planning | `backend/Streams/MultiSegmentStream.cs`, `FiniteRangeSegmentPlan.cs`, `AdaptiveBodyBatchSizer.cs` | Remain the only owners of active stream article read-ahead. No second donor read-ahead loop. |
| Deduplicating overlapping readers | `backend/Services/SharedStreamRegistry.cs`, `backend/Streams/SharedStreamEntry.cs` | Keep shared readers/ring buffers; integrate one file-cache wrapper below the shared pump. |
| NNTP capacity, fallback, priority, quotas | `backend/Clients/Usenet/DownloadingNntpClient.cs`, `StreamingCapacitySnapshotProvider.cs`, `MultiProviderNntpClient.cs` | Reuse actual admission and provider pools. A capacity snapshot is only a hint, not a permit. Do not port donor Scheduler v2. |
| Memory and bandwidth limits | `backend/Streams/InFlightArticleBudget.cs`, `UsenetBandwidthLimiter.cs`, `SharedStreamRetentionAccount.cs` | Reuse current limits. Account new cache buffers separately and expose the aggregate; never count ring memory as article-budget memory. |
| Release discovery/readiness warming | `backend/Services/Watchtower/WatchtowerService.cs`, `PreflightOrchestrator.cs`, `PreflightCache.cs` | Retain discovery, NZB fetch, validation, and list ownership. New predictions warm bytes of already imported DAV items only. |
| Health, negative results, repair | `ArticleMissNegativeCache.cs`, `backend/Services/Repair/RepairPatchStore.cs`, `PlaybackFastVerifier.cs`, `backend/Streams/Par2VerifiedFileStream.cs` | Reuse existing verdicts and repaired-article precedence. Cache must not become a second repair system or hide a failed provider probe. |
| Read tracking and diagnostics | `backend/Services/ConcurrentReadTracker.cs`, `Services/StreamTrace/`, `Services/Observability/`, `Services/SupportPack/` | Extend these surfaces with cache/prefetch fields, not a duplicate debug dashboard. |
| rclone VFS buffering and setup advice | `docs/configuration/streaming.md`, `frontend/app/routes/setup/` | Preserve existing recommendations. Enabling native cache is an explicit advanced choice with overlap warnings. |

The NNTP client chain already places repaired segments outside the article cache
and downloading client (`UsenetStreamingClient.CreateDownloadingNntpClient`).
Keep that precedence. Preserve exactly-once completion callbacks, ordered batch
responses and bounded ordered fallback; cached responses must not acquire network
permits merely to report a hit.

## Donor feature disposition

The donor's `SmartPrefetchService.cs` is 11,960 lines, `CacheService.cs` 5,887,
and `NativeCacheStream.cs` 4,286 at the inspected SHA. They mix policy, I/O,
timers, playback integration, diagnostics, and recovery. Treat them as behavior
references and regression sources, not files to copy wholesale.

| Donor behavior/reference | Destination and delivery stage |
| --- | --- |
| Full and partial final-file hits; byte-range miss fallback (`NativeCacheStream`, `ICacheService`) | New native-cache module, first functional slice. Direct NZB first, archive/decryption eligibility next. |
| Range coverage and chunk geometry (`CacheService`, `CacheServiceWriteIntegrityTests`, `CacheServiceOverlapWriteTests`) | Versioned immutable extents and manifest, atomic publication, strict length/hash checks. No legacy layout compatibility by default. |
| Multiple folders, quotas, availability/failover | Single local writable root first; optional multi-root policy after core canary. Read-only roots and network filesystems need separate capability tests. |
| Cache browse, eviction, integrity verification, provenance | Authenticated operations on native entries; compact generation/hash/source evidence. Do not port legacy verification database tables or automatic repair workflows. |
| Read-through and minimum/head/tail warm | New range executor and admission policy. Never count physical sparse length as valid coverage. |
| Manual range/full-file warm, cancellation, queue visibility | One bounded prefetch queue; full-file jobs process resumable windows. |
| Verified playback, resume and next episode (`RecentPlaybackPriorityTracker`, `EpisodePredictionService`) | Optional adapters and pure prediction policy after cache/scheduler gates. |
| Watch-history, movie/TV predictions, collections/hubs | Later opt-in producers sharing the same queue, budgets, and imported-item resolver. Reuse Watchtower list sources where available. |
| Active stream read-ahead | Already supplied by target; reuse, do not port. Read activity may record demand but does not authorize parallel speculative fetches for the same window. |
| Rclone cache service, Plex initial scans, source-map repair lanes | Out of scope as feature ports. They are separate systems and cannot be implicit dependencies. |

Legacy regression names are leads: first inspect whether the behavior and its fix
exist at the pinned donor SHA. In particular, this checkout's
`RecentPlaybackPriorityTracker.MarkRecentlyWatched` refreshes expiry on updates
even when preserving a stronger source. Do not assume historical fixes or
eviction-lease classes from other donor branches are present here.

## Alternatives

1. **Recommended: additive final-file cache plus thin predictive producers.**
   Preserves current article and streaming layers, avoids repeat archive/decrypt
   work on file-cache hits, and creates explicit policy boundaries. Costs a new
   persistent format and careful invalidation/fidelity integration.
2. **Warm only the existing article cache.** Least code and storage-format work;
   useful as a benchmark control. Does not provide exact final-file coverage,
   direct assembled-file hits, or the same benefit for encrypted/archive content.
3. **Port donor services intact.** Fastest apparent feature parity, but imports
   parallel schedulers, repair assumptions, timers, static singleton state, and
   overlapping UI/configuration. Reject for this integration.

## Intended read and write path

```text
WebDAV GET / existing /view controller
  -> existing range/auth/HEAD handling
  -> existing shared-stream attach OR private stream
  -> NativeFileCacheStream (once per underlying stream)
       hit: verified final-file extent -> consumer
       miss: existing NZB / multipart / archive / AES stream
               -> repaired articles -> article cache -> existing NNTP admission
            -> validated final bytes -> bounded native writer -> consumer

Optional prediction/manual intent
  -> PrefetchQueue -> admission -> bounded range executor
  -> same stream factory + native writer, background workload, no HTTP self-call
```

Do not wrap both each shared reader and its pump. Preserve `GetAndHeadHandlerPatch`
and `GetWebdavItemController` behavior for HEAD, 416, cancellation, and small ranges.
The native wrapper must sit after AES decoding for encrypted files. The raw
stream factory is lazy, so a complete native hit does not open archives or NNTP.

Extract the duplicated content-opening responsibilities from
`DatabaseStoreNzbFile`, `DatabaseStoreRarFile`, and `DatabaseStoreMultipartFile`
into a single scoped `DavContentStreamFactory`. Keep those classes as thin DAV
adapters. Do not alter format parsing or replace `LazyRarResolver`.

`BaseStoreStreamFile.CreateStreamingScope` currently always assigns high priority.
It cannot be reused unchanged by a background warmer. Extract an explicit
purpose-aware scope: foreground retains today's semantics; warming uses existing
background/low priority and the same provider, bandwidth, and article permits.
Keep token-keyed contexts alive until stream disposal and dispose exactly once.

## Native storage, identity, and integrity

Proposed initial layout: `<root>/native-v1/<identity-hash>/manifest.json` and
immutable extent files. An extent is at most 4 MiB; the final extent can be short.
Geometry is stored per entry. A sparse write is an extent with an explicit
half-open `[start,end)` range, not a preallocated file advertised as complete.
Coalesce adjacent extents asynchronously when worthwhile; never rewrite the
entire cache file for a small read. Cap fragments per block and entry; decline
new caching when fragmented rather than grow metadata without bound.

Identity includes DAV item ID, blob identity, final size, manifest format, and a
stable content revision covering ordered article mapping, archive member,
encryption parameters (hashed, never logged), and repair/mapping generation.
`SharedContentIdentity(UniqueKey, FileBlobId, FileSize)` is useful but alone does
not prove that same-size repaired or remapped bytes are unchanged.

Centralize content invalidation with existing blob/repair update paths. Before
returning cached bytes, compare entry revision with current metadata. Invalidate
before publishing a new mapping/patch; fence racing writes and active shared
entries. If a repair cannot be mapped precisely to affected files, use a
conservative persistent global revision fence. Broad invalidation is acceptable;
stale bytes are not. Validate restart behavior, not just in-memory generations.

Initial metadata authority is a versioned on-disk manifest and rebuildable bounded
catalog, not new main-database tables. This avoids importing the donor schema
and works with SQLite and PostgreSQL target installs. Multi-instance access to
one native root is unsupported: acquire an exclusive owner lock; fail enabling
the cache with an actionable warning if already owned.

Publication protocol:

1. Acquire the entry activity lease and bounded writer-memory/disk reservation.
2. Obtain exact-range fidelity evidence from the final stream; reject synthetic
   zeros/null packets, short reads, untrusted maps, and failed source verification.
3. Write a uniquely named temporary extent; verify exact count and compute hash;
   flush and atomically rename on the same filesystem.
4. Publish a replacement manifest referencing only completed immutable extents.
   Serialize publication with invalidation; recheck revision before commit.
5. Expose coverage only after publication. Release each buffer/reservation once.

Checksums prove local storage integrity, not correctness of a wrong source map.
Carry stream provenance separately. In-process shared pumps and wrappers must
preserve it. Introduce exact-range fidelity reporting at actual gap-fill and
verification boundaries; a heuristic zero scan or a global recent-error flag
is insufficient. Until a stream type can supply evidence, bypass native writes
for it. Fully verified PAR2/repaired data remains eligible.

Missing/malformed manifests, checksum mismatch, truncation, unknown versions,
or revision mismatch are misses with bounded diagnostics. Quarantine only the
affected native generation. Do not modify the underlying DAV item or trigger
Arr replacement. Startup recovery ignores incomplete temporaries, prunes only
owned orphan files after a grace period, and reconciles quota accounting.
Atomic rename is not a blanket power-loss guarantee: verify extents on reads and
after interrupted restart; document filesystem durability limits.

Hold shared entry leases across reads and writes; automatic eviction needs an
exclusive lease. Manual eviction marks the generation retired and rejects new
leases, then waits for current leases with a bounded timeout. Return a conflict
if busy; never unlink a live file to satisfy a UI action. Expired locks/idle
entries must be removed without ABA or unbounded dictionary growth.

## Preventing duplicate storage and fetches

- Native cache remains off by default. Existing segment-cache defaults, settings,
  and old content are untouched.
- On eligible native-owned reads, use a scoped admission context that allows
  existing article hits but suppresses *new* article-cache writes for those
  bytes. Foreground validation/repair/probes and noneligible stream types keep
  their existing policy. A native write failure falls back to uncached delivery;
  later requests can retry. Do not change global cache settings on failures.
- The new context must propagate through cancellation-token replacements,
  detached streams, and shared pumps. Do not overload provider-attribution
  bypass, because it intentionally bypasses local sources for verification.
- Coalesce foreground and warm misses by content revision and block/range.
  Followers may cancel independently. Foreground demand promotes a queued warm
  request and uses a short bounded wait on active work; it may take over after
  timeout. Count such duplicate fetches explicitly rather than deadlock a seek.
- Historical article entries and rclone caches may still overlap. Report the
  separate physical byte totals; do not claim zero disk duplication or silently
  delete them. rclone configuration is not changed by this feature.
- Verification/provenance requests explicitly bypass the final-file cache,
  retaining the current policy for whether repaired/article data is acceptable.

## Prefetch decisions and bounded execution

Separate intent generation, queueing, admission, and execution. Use DI and
`BackgroundService`/awaited timers; no static `Instance`, async-void timers,
unbounded queues, or per-read `Task.Run` calls.

An intent records content revision, range, source, confidence, creation/expiry,
priority, retry count, and cancellation identity. Deduplicate overlapping ranges;
never use filename-only matching. Queue order: manual explicit requests and
verified resume/next-episode requests, then history predictions, then collections
and minimum warming. Every category remains below actual foreground playback.
Use aging only within the background queue; it cannot promote speculation to
foreground NNTP priority.

Start with one worker, at most 256 queued intents, a 64 MiB execution window,
32 MiB of physically rented native-writer buffers, and a 10 GiB daily speculative
budget. These are proposed testing defaults, not measured optima. Count attempted
provider bytes against budget, including failures; recheck at each window and
cap boundary overshoot to one admitted window. Reuse actual ingress telemetry;
do not infer bandwidth from final-file length. Manual full-file warming needs an
explicit size/budget preview and uses the same limits.

Pause admission under disk-reserve exhaustion, writer saturation, memory pressure,
unhealthy provider capacity, or foreground wait pressure. Cancellation and
preemption occur between bounded ranges and through normal NNTP completion;
never manufacture connection release. Bound retries (three per range with jitter)
and honor existing missing/corrupt verdicts. Deferred work has a reason, next
eligible time, and expiry; repeated failure cannot reinsert work indefinitely.

Persist only explicit manual full-file jobs in a compact versioned journal;
restart them paused pending operator resume. Recompute speculative jobs from
bounded fresh snapshots. Published coverage is authoritative; progress is
committed unique bytes, not bytes requested or a historical percentage.

Verified playback requires a fresh authenticated Plex/Emby session and exact
mapping to an imported item. Select the active Media/Part, reject ambiguous
matches, and account for quality versions. Raw sustained WebDAV reads are demand
signals, not verified playback, and cannot extend verified-session expiry.
Keep the two expiries separate. Adapters are optional; manual warming works
without media-server credentials. Jellyfin is a separate adapter contract test,
not assumed compatible because an Emby endpoint looks similar.

Prediction phases include next episode/season transition, resume, bounded
watch-history scoring, movies/TV, collections, and opt-in head/tail minimum
warming. Resolve already imported content using exact IDs and configured path
mapping. Unavailable items stay unavailable; do not add a new downloader,
Sonarr search loop, or list crawler that duplicates Watchtower/Arr.

## Configuration, UI, and operational behavior

Use target dotted `ConfigKeys`, typed defaults, environment ownership,
`ManagedSetting`, secret masking, existing settings loaders/actions, and generated
admin contracts. Do not copy donor `cache_enabled` or `SmartPrefetch.*` keys.

Initial settings: `cache.native.enabled=false`, `cache.native.path` under
`CONFIG_PATH` but disjoint from segment/repair storage, `cache.native.max-gb=20`,
`cache.native.minimum-free-gb=2`, `cache.native.writer-mb=32`,
`prefetch.enabled=false`, `prefetch.mode=manual`, `prefetch.daily-budget-gb=10`.
Path, enablement, and writer geometry require restart in the first version;
prefetch pause/mode and validated budget changes can apply live. Restart
requirements must appear before Apply. Reject overlapping/ancestor roots,
symlink escapes, invalid capacities, or enabling prefetch without a writable
native sink. Preserve cache files on disable; removal is an explicit operation.

Place cache controls in existing Streaming settings and operational cache/job
views within existing navigation. Separate article-cache and file-cache totals.
Expose paginated native entries/coverage, queued/running/deferred/failed jobs,
source, reasons, pause/cancel/resume, and busy eviction outcomes. No broad
arbitrary-filesystem cache APIs. Mutation endpoints require existing admin auth.

Setup review: both features remain advanced and off for new installations.
Do not bump wizard version solely for optional features. Update Review and
server-side strategy validation to warn about native+segment+rclone stacking
when enabled and honor environment-owned settings. A wizard completion cannot
silently turn off an environment-managed value or enable warm downloads.
Document plaintext cache-at-rest implications for decrypted content; use private
directory/file permissions and exclude cache payloads, titles, and tokens from
support packs. Add introducing-release pills only when the release is assigned;
planning documents must not assert a shipped version.

## Success gates and rollout

Correctness gates are mandatory: byte-for-byte parity with cache disabled,
zero synthetic-data admission, safe cancellation/eviction/restart, exactly-once
completion, no stale hits after repair/remap, zero provider BODY calls for fully
cached ranges, and bounded memory/queue/catalog growth. Run the same checks with
shared streams on/off, segment cache on/off, plain/multipart/encrypted content,
and SQLite/PostgreSQL metadata where relevant.

Measure on the same host with a fixed corpus and cold/warm states recorded.
Proposed acceptance thresholds: disabled-path p95 latency and throughput within
5% of baseline; foreground p95 startup/seek under background warming within 10%
of baseline and throughput at least 95%; cache-hit reads fetch zero provider
bytes and avoid archive/decryption work; all reservations return to zero after
quiescence. Use at least 30 repeated reads per timing scenario, report medians,
p95, dispersion, and raw samples. Thresholds are release decisions, not flaky
wall-clock assertions in PR CI. If noise obscures the comparison, collect more
samples; do not report an optimization win.

Roll out cache-off baseline, manual cache, read-through, verified next episode,
then history/collections. Each stage has separate counters and an immediate
prefetch pause. Build a uniquely tagged testing image with the existing custom
image workflow or locally; do not move `dev`, `rc`, `latest`, or production mounts.
Use a separate config/cache root and private test ports; no writable production
database, Arr callbacks, or Plex library changes. Roll back to the recorded
baseline image and disable the optional features, retaining test artifacts.

The [implementation plan](../plans/2026-09-20-smart-prefetch-native-cache.md)
defines file ownership, sequencing, tests, and the canary procedure. Completing
the planning PR does not authorize merging an implementation PR or deploying it.
