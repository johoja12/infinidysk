# Smart prefetch and native file cache integration design

Status: user accepted the direction for detailed planning; implementation and
performance validation remain outstanding. No feature deployment.
Date: 2026-09-20.

User requirements clarified on 2026-09-20: Segment and Native are mutually
exclusive cache modes. Multi-folder native-cache UI and configurable Plex feature
parity (including login and hubs) are required delivery scope, not optional
follow-ups. This revision supersedes the initial coexistence/single-folder design.
The user also confirmed a 50 TB HDD/NAS cache holding mostly whole movies/episodes.
The [50 TB comparison](2026-09-20-50tb-cache-architecture-comparison.md) recommends
native final-file caching with one sparse data file per media generation and a
local persistent catalogue, superseding the initial 4 MiB-per-file extent layout.

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
| Persistent decoded articles keyed by Message-ID | `backend/Clients/Usenet/SegmentCacheNntpClient.cs` | Keep implementation, layout and mixed-hit batch overlay in Segment mode only. Native mode must neither instantiate nor read/write this cache. |
| Bounded asynchronous article writes | `backend/Clients/Usenet/SegmentCacheWriteBehind.cs` | Retain; reuse reservation/ownership patterns, not its article-specific queue for file extents. |
| Article-cache metrics and cleanup | `SegmentCacheStatistics.cs`, `backend/Services/SegmentCacheCleanupService.cs` | Retain metrics; make cleanup mode-aware so a switch to Native does not purge inactive segment data. Native storage uses distinct roots and format markers. |
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
| Multiple folders, quotas, availability/failover | Required in the first usable native-cache UI: folder CRUD, priorities, quotas, age limits, enabled/read-only flags, storage types, scans and per-folder stats. Network filesystem behavior needs capability tests. |
| Cache browse, eviction, integrity verification, provenance | Authenticated operations on native entries; compact generation/hash/source evidence. Do not port legacy verification database tables or automatic repair workflows. |
| Read-through and minimum/head/tail warm | New range executor and admission policy. Never count physical sparse length as valid coverage. |
| Manual range/full-file warm, cancellation, queue visibility | One bounded prefetch queue; full-file jobs process resumable windows. |
| Plex login, server discovery/testing, libraries, hubs and source previews (`PlexAuthController`, `PlexMovieSourceService`, `PlexTvSourceService`) | Required configurable Plex integration with native InfiniDysk auth/secret handling and settings UI. |
| Verified playback, resume and next episode (`RecentPlaybackPriorityTracker`, `EpisodePredictionService`) | Required Plex adapter and pure prediction policy after cache/scheduler gates; runtime enablement remains opt-in. |
| Watch-history, movie/TV predictions, collections/hubs | Required configurable producers sharing the same queue, budgets, and imported-item resolver. Reuse Watchtower list sources where available; do not omit Plex-specific hubs. |
| Active stream read-ahead | Already supplied by target; reuse, do not port. Read activity may record demand but does not authorize parallel speculative fetches for the same window. |
| Rclone cache service, Plex initial scans, source-map repair lanes | Out of scope as feature ports. They are separate systems and cannot be implicit dependencies. |

Legacy regression names are leads: first inspect whether the behavior and its fix
exist at the pinned donor SHA. In particular, this checkout's
`RecentPlaybackPriorityTracker.MarkRecentlyWatched` refreshes expiry on updates
even when preserving a stronger source. Do not assume historical fixes or
eviction-lease classes from other donor branches are present here.

## Alternatives

1. **Recommended: additive final-file cache plus thin predictive producers.**
   Preserves the article cache as an alternative mode and current streaming layers, avoids repeat archive/decrypt
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
               -> repaired articles -> existing NNTP admission (Native mode)
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

Proposed layout for the confirmed whole-media HDD/NAS workload:
`<root>/native-v1/<identity-hash>/<generation>/` containing one `content.data`,
`manifest.json` and a bounded range-commit journal. The data file can be sparse
while filling; only verified half-open `[start,end)` ranges are readable.
Published ranges are immutable, with integrity units at most 4 MiB, not separate
4 MiB filesystem files. Filling completes the same data file without copying it.
Use bounded streaming buffers independent of media size. Persist layout per entry;
benchmark 64/256 MiB containers as a Native-mode fallback where NAS sparse behavior
is unsuitable. Never rewrite a whole movie for a small read. Cap fragments per
entry and coalesce range records, not full media data, during checkpoints.

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

Metadata authority is a versioned on-disk manifest plus checksummed range journal,
with a rebuildable persistent indexed catalogue and bounded hot in-memory state,
not new main-database tables. Keep the auxiliary catalogue on local disk (SSD
preferred), even when media data is on NFS/SMB; do not place SQLite WAL on a network
mount. Batched single-writer updates, indexed eviction candidates and checkpointed
recovery avoid a full catalogue sort/scan on normal startup or eviction. This
avoids importing the donor schema
and works with SQLite and PostgreSQL target installs. Persist the configured folder
list through the existing settings store, with stable folder IDs. Each entry records
its owning folder; one file generation has one writable owner. Choose an enabled,
writable, healthy folder by priority and available quota/free space, with stable ID
tie-breaking. An unavailable folder permits placement of new entries elsewhere;
existing entries fall back to source rather than silently relocating or splitting.
Multi-instance writing to one root is unsupported: acquire an exclusive owner
lock on writable roots. Read-only roots never receive locks or other disk writes;
accept only a compatible immutable snapshot, not another instance's live store.

The required folder editor matches donor `CacheFolder` and
`frontend/app/routes/settings/native-cache/native-cache.tsx`: name, path, maximum
size, maximum age (zero means no age limit), priority, enabled, read-only, and
Local/NFS/SMB/Unknown type. Include add/edit/remove, probe/test, scan/reconcile,
explicit clear, usage/file/chunk counts, availability/error state, and per-folder
eviction results. Registration/removal does not implicitly move or delete files.
Read-only supports compatible native-v1 snapshots; it is not a promise of donor
cache-format migration. Validate NFS/SMB owner exclusion and atomic publication;
reject enabling writes where those capabilities cannot be demonstrated. Reject
duplicate/overlapping roots even across symlink aliases. Root edits/removal drain
leases and require an explicit choice to retain data or clear owned entries.

Publication protocol:

1. Acquire the entry activity lease and bounded writer-memory/disk reservation.
2. Obtain exact-range fidelity evidence from the final stream; reject synthetic
   zeros/null packets, short reads, untrusted maps, and failed source verification.
3. Serialize writes to unpublished gaps in the owning data file, using bounded
   buffers; verify exact count/hash and flush before any range commit. Never
   overwrite an already published span; discard duplicate writes after comparison.
4. Append a checksummed durable range-commit record and periodically atomically
   checkpoint the manifest. Serialize with invalidation and recheck revision.
   Reject incomplete journal tails. Index updates are recoverable from this evidence.
5. Expose coverage only after durable publication. Release each buffer/reservation
   once. Persist dirty-generation tracking before mutation so normal recovery
   visits dirty entries, not every file on the NAS; lost indexes rebuild separately.

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
after interrupted restart; document filesystem durability limits. Account allocated
blocks, journals, retired generations and in-flight reservations, not sparse apparent
length. Roots on one filesystem share its free-space floor. Evict whole unleased
cold media generations in bounded indexed batches, with configurable high/low
watermarks; do not generate millions of article-level deletion operations.

Hold shared entry leases across reads and writes; automatic eviction needs an
exclusive lease. Manual eviction marks the generation retired and rejects new
leases, then waits for current leases with a bounded timeout. Return a conflict
if busy; never unlink a live file to satisfy a UI action. Expired locks/idle
entries must be removed without ABA or unbounded dictionary growth.

## Exclusive cache modes and preventing duplicate fetches

- One server-side effective `cache.mode` is `off`, `segment`, or `native`.
  Segment mode uses today's article-cache wrapper and no native reader/writer.
  Native mode uses the final-file cache and no article-cache wrapper or catalog,
  including old article hits. Off instantiates neither. Repair patches and
  shared in-memory buffers remain independent correctness/streaming mechanisms.
- UI exposes one selector, never two independent enable switches. Persist mode
  atomically; reject conflicting API, environment, import and wizard settings.
  A native-cache failure falls back to the ordinary source stream, never Segment.
- When `cache.mode` is absent, derive Segment/Off from the existing effective
  `usenet.segment-cache.enabled` value so existing installs keep their behavior.
  Explicit mode takes ownership; the legacy key becomes a validated compatibility
  alias, not a second switch. Reject contradictory effective environment values
  with the exact key names to fix; do not silently override environment ownership.
  Convert persisted legacy state atomically on first explicit mode save. Legacy
  API updates map through the same mode validator; they cannot enable both.
- Mode changes require restart initially. Show pending versus active mode;
  stop admission and drain old cache workers during shutdown before activating
  the new mode. Test startup, shutdown and client reconfiguration so both cache
  services are never active together. No opportunistic live fallback switching.
- Switching modes retains inactive files but never serves them. Guard existing
  `SegmentCacheCleanupService` against the Native transition; preserve documented
  legacy disabled-cache cleanup only for legacy configuration with no explicit
  mode. Explicit Off also retains files until an explicit owned-cache clear.
  Describe the new selector's retention behavior before Apply and in release notes.
- Coalesce foreground and warm misses by content revision and block/range.
  Followers may cancel independently. Foreground demand promotes a queued warm
  request and uses a short bounded wait on active work; it may take over after
  timeout. Count such duplicate fetches explicitly rather than deadlock a seek.
- Historical inactive files and external rclone caches may remain on disk.
  Report inactive retained bytes separately from active cache statistics; no
  combined hit path or simultaneous cache operation. rclone configuration is
  not changed by this feature.
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
Keep the two expiries separate. Plex support is required product scope but its
activation is optional; manual warming works without media-server credentials.
Emby/Jellyfin extensions must not delay or substitute for Plex parity.

## Required Plex parity and configuration

Use these donor references as the acceptance inventory:
`backend/Api/Controllers/PlexAuth/PlexAuthController.cs`,
`TestPlexConnection/`, `SmartPrefetch/SmartPrefetchController.cs`,
`backend/Services/PlexMovieSourceService.cs`, `PlexTvSourceService.cs`,
`PlexVerificationService.cs`, `WatchHistoryService.cs`, and frontend
`routes/settings/smart-prefetch/{smart-prefetch.tsx,PlexMovieCategories.tsx,PlexTvCategories.tsx}`.
Inspect the integrations login UI during implementation as well. Parity is
user-visible capability, not identical endpoints, donor bugs or giant services.

| Capability | Required UI and behavior | Acceptance evidence |
| --- | --- | --- |
| Plex login | Sign in via PIN/browser flow, pending/expired/cancel/retry states, reconnect/disconnect; manual server URL/token remains available | Mock PIN lifecycle and real opt-in login canary; no password collection |
| Servers | Discover owned/shared servers and local/remote/relay connection candidates, select/test/save multiple servers, stable machine IDs and path mappings | Multiple servers, failed candidate, auth expiry, rename and per-server token tests |
| Libraries and users | Fetch/select movie and TV libraries, scoped user/history sources and identity mappings; manual refresh and last-success/error status | Separate server/library/user IDs, pagination and partial permission tests |
| Movie and TV sources | Fetch global and library-specific hubs and collections; preview members before enabling, refresh, save per-source enabled flag and item limit | Continue Watching/On Deck/Recently Added when returned by the server; empty/missing/renamed hubs handled |
| TV controls | Configure episodes per show, queue-ahead count and per-source excluded shows; select next unwatched/next season | Exclusions and multi-episode/season boundaries preserved after reload |
| Realtime and history | Independent enable switches, polling/sync intervals, lookback, minimum episodes, confidence/cooldown, manual sync | Fake clock proves limits, no overlapping sync, stale signals expire |
| Trigger controls | Verified Plex playback, read activity, predictions, minimum warm, collections/hubs, movie warming and TV warming separately configurable | Disabled triggers emit no new intents; disabling a source retires its queued jobs |
| Work limits | Concurrent warming, connection/per-item caps, daily byte budget, warm size/mode and warm-local-file eligibility | Limits stay within existing global NNTP admission; local-file option never bypasses exact DAV mapping or starts a second rclone warmer |
| Operations | Preview predictions with source/reason, manual/bulk warm, reorder background queue, pause/resume, cancel/retry, bounded history and failed-job views | API/UI contract tests; reordering cannot outrank foreground reads |

Source selections are keyed by server machine ID + library ID + kind + stable
hub/collection ID, not display names. Fetch Plex sources directly where Watchtower
has no equivalent; share normalized imported-item mapping and the existing queue
where it does. Persist configurable controls via typed dotted keys/JSON settings,
provide help and defaults, and verify read-back after save/restart.

Split Plex account authentication, server discovery, catalogue retrieval,
playback/history polling and source policy into separate DI services. Use bounded
pagination, cached snapshots, cancellation, coalesced refresh and retry backoff;
movie/TV providers share the transport and source parser. Login sessions are
short-lived and bound to the initiating authenticated admin session. Retain a
stable installation client identifier. Store credentials server-side, return
opaque account/server handles and masked status, and never copy donor logging of
PIN codes/token prefixes or token-bearing URLs. Send tokens in headers; constrain
source keys/redirects to the selected configured server so previews cannot forward
credentials to another origin. Private LAN server URLs are supported explicitly.

All these capabilities must be delivered for the smart-prefetch feature to be
complete; a manual warmer or token-only Plex poller is an intermediate milestone.

Prediction phases include next episode/season transition, resume, bounded
watch-history scoring, movies/TV, collections, and opt-in head/tail minimum
warming. Resolve already imported content using exact IDs and configured path
mapping. Unavailable items stay unavailable; do not add a new downloader,
Sonarr search loop, or list crawler that duplicates Watchtower/Arr.

## Configuration, UI, and operational behavior

Use target dotted `ConfigKeys`, typed defaults, environment ownership,
`ManagedSetting`, secret masking, existing settings loaders/actions, and generated
admin contracts. Do not copy donor `cache_enabled` or `SmartPrefetch.*` keys.

Initial settings: `cache.mode` with legacy-preserving resolution and Off for new
installs, `cache.native.folders` as a validated list of stable folder records
(empty by default; UI proposes a disjoint CONFIG_PATH folder with 20 GiB quota),
`cache.native.minimum-free-gb=2`, `cache.native.writer-mb=32`,
`prefetch.enabled=false`, `prefetch.mode=manual`, `prefetch.daily-budget-gb=10`.
Add a verified-local `cache.native.metadata-path` for the auxiliary catalogue.
The small test quota/free-space defaults are not recommendations for a 50 TB pool;
the folder UI must support 64-bit byte quotas and configurable headroom/watermarks.
Support retained/pinned versus evictable media policy for whole-media retention;
protected content can exhaust admission but must not trigger unrequested deletion.
Mode, folder configuration, and writer geometry require restart in the first version;
prefetch pause/mode and validated budget changes can apply live. Restart
requirements must appear before Apply. Reject overlapping/ancestor roots,
symlink escapes, invalid capacities, or enabling prefetch outside Native mode or
without an enabled healthy writable native folder. Preserve cache files on an
explicit mode switch; removal is an explicit operation.

Place the cache-mode selector in existing Streaming settings, with a native
folder editor and configurable Plex/Smart Prefetch settings in existing navigation.
Show the active mode's metrics and inactive retained files separately.
Expose paginated native entries/coverage, queued/running/deferred/failed jobs,
source, reasons, pause/cancel/resume, and busy eviction outcomes. No broad
arbitrary-filesystem cache APIs. Mutation endpoints require existing admin auth.

Setup review: Native and Smart Prefetch remain advanced and off for new installations.
Do not bump wizard version solely for optional features. Update Review and
server-side strategy validation to enforce Segment-or-Native exclusivity and warn
about the selected cache plus rclone buffering; honor environment-owned settings.
A wizard completion cannot
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
shared streams on/off, each cache mode, plain/multipart/encrypted content,
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

Roll out cache-off/Segment baselines, multi-folder Native/manual cache, read-through,
Plex login/discovery/source previews, verified next episode,
then history/collections. Each stage has separate counters and an immediate
prefetch pause. Build a uniquely tagged testing image with the existing custom
image workflow or locally; do not move `dev`, `rc`, `latest`, or production mounts.
Use a separate config/cache root and private test ports; no writable production
database, Arr callbacks, or Plex library changes. Roll back to the recorded
baseline image and disable the optional features, retaining test artifacts.

The [implementation plan](../plans/2026-09-20-smart-prefetch-native-cache.md)
defines file ownership, sequencing, tests, and the canary procedure. Completing
the planning PR does not authorize merging an implementation PR or deploying it.
The [execution contracts](../plans/2026-09-20-cache-prefetch-execution-contracts.md)
define cache-mode resolution, wizard behavior, persistence transitions, Plex
configuration and dependency gates for implementation.
