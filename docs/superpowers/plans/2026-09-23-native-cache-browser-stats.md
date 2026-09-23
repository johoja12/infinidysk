# Native cache browser and statistics plan

## Goal and reference

Add a dedicated Native Cache page in InfiniDysk with the three NZBDAV views shown in the reference screenshots: Overview, All Files, and Recently Evicted. The Overview includes global usage and 24-hour traffic, folder usage, active writes, and recent file activity. Keep Settings > Streaming as the place to configure folders and run maintenance jobs; link to it from the new page.

Use `/opt/projects/nzbdav/frontend/app/routes/stats/components/NativeCacheStats.tsx` and `/opt/projects/nzbdav/backend/Api/Controllers/Cache/CacheController.cs` as behavior references. Build the page with InfiniDysk's existing components and API conventions. The underlying cache models differ, so copy the concepts and labels, not the donor implementation or numbers.

## Current InfiniDysk baseline

| Capability | Current state | Work needed |
| --- | --- | --- |
| Cache mode, folder health and allocated bytes | `GET /api/native-cache`; includes folder status and process counters | Show on Overview; join configured quotas and folder labels from the saved settings. Show active versus saved mode and restart warning. |
| Entry listing | `GET /api/native-cache/entries` requires one folder and pages by cache key | Add global server-side search, filters, sorting, and stable pagination. Keep the existing endpoint for the Settings folder inspector. |
| Coverage gaps | `GET /api/native-cache/ranges` pages verified ranges | Reuse in a file detail panel; present covered ranges and holes without reading payloads. |
| Traffic counters | `NativeCacheStatistics` has lifetime, process-local hit blocks/bytes, misses, committed bytes, fallbacks and I/O timeouts | Add windowed 24-hour counters and show the process scope of existing counters accurately. Miss bytes and transfer speeds require new instrumentation. |
| Activity and evictions | Entries have coarse `Access` buckets; no access count, recent activity query, transfer registry, or eviction log | Add bounded metadata and read-only APIs; preserve cache I/O safety. |
| Actions | Folder clear/probe/scan and entry pin are available under Settings | Link to existing controls. Keep bulk eviction and destructive actions out of the first browser release. |

`NativeCacheStore.GetStatusAsync` currently counts live entries **plus retired entries** in folder allocated bytes/count. The browser must identify that distinction; a retired entry is allocation debt, not a playable cached file. The global file count must come from live `Entries` with verified bytes, and an optional retained/retired count must have its own label. Folder size may include retired bytes to reconcile with quota use.

## Product behavior

### Placement and layout

Add `/native-cache` to the main navigation near Library/Health. The top bar shows current cache mode, storage health and last refresh. Three tabs mirror the screenshots:

1. **Overview:** compact global cards for allocated/quota, live files/verified blocks, 24-hour hit rate and bytes saved; a folder table with capacity bars, type, priority, health, file count, and a Manage Folders link; active writes; recent activity. On narrow screens, cards wrap and tables scroll horizontally.
2. **All Files:** a searchable, sortable, paged table with name, folder, verified coverage, file size, allocated size, last access, and pin state. Filters: folder and complete/partial/empty. Clicking a row opens a detail panel with verified ranges/gaps and a link to the library item when resolvable. Show an explicit distinction between `verified bytes / logical file size` and physical allocated bytes. Exclude zero-verified catalogue rows from the default file count but allow an `empty` filter for diagnosis.
3. **Recently Evicted:** paged history with name, folder, reason, verified bytes before eviction, last access and eviction time. Filter by name, folder and reason. Show only confirmed deletions; skipped pinned/leased entries and failed unlink attempts do not appear as evictions.

The page polls its lightweight Overview endpoint while visible, initially every 5 seconds for transfers and every 30 seconds for summary/activity. File search and eviction history load on tab entry or filter change; cancel stale requests. Pause polling when the tab is hidden. Display a timestamp and separate error/unknown states; never turn an offline folder or unavailable statistic into a green zero. Do not reveal full server filesystem paths outside the existing authenticated admin UI.

### Metric definitions

| Metric | Definition |
| --- | --- |
| Allocated / quota | Current catalogue allocation, including retired allocation debt, divided by sum of enabled configured folder quotas. Label physical free-space reserve separately; do not add it to quota. |
| Live files / verified blocks | Distinct live cache entries with `VerifiedBytes > 0`; count verified block rows. Do not treat an entry with no verified bytes as a cache hit. |
| 24-hour hit rate | `hit blocks / (hit blocks + missed blocks)` over a rolling UTC 24-hour window, with `—` when no requests occurred. Also show hit and miss counts; distinguish block-level rate from file-level rate. |
| Bandwidth saved | Bytes actually served from verified native cache blocks over the same window. Label it as an estimate of avoided source transfer. |
| Cached coverage | Sum of verified ranges, clamped to file length. This is independent of where the holes are; detail shows their positions. |
| Active writes | Committed/verified new bytes during an in-flight cache fill, total logical file size, recent committed-byte rate, duration and target folder. Counts must not include bytes merely buffered or queued for fsync. |
| Recent activity | Entries ordered by last real cache access. Display access count only after it is instrumented; legacy rows show `—`, not zero. |

## Backend delivery

### 1. Read models and queries

Extend the local native-cache catalogue in `backend/Services/NativeCache/NativeCacheStore.cs` with additive metadata for access counts and eviction events, plus indexes for access/time, folder, and eviction time. Use a bounded retention period and/or row cap for the event history. Keep the catalogue under the configured local metadata path; do not move it to the media folder or EF database. An additive schema update needs a `/config` backup note in the implementation PR and release notes. Legacy access counts are unknown. Migration must avoid a full 50 TB scan and start in bounded time.

Add server-side queries for aggregate live/retired bytes, live files, verified blocks, recently accessed entries, global filtered file pages and eviction pages. Filter and sort in SQLite before limiting; never pull every entry into the frontend or enumerate payload directories for page loads. Store a searchable display-name snapshot with each entry when a `DavItem` opens the cache, and refresh it when the item is observed again. Backfill older rows from `DavDatabaseClient` in bounded batches on a background path; expose backfill progress and allow ID search until names are filled. If a DavItem is gone, retain the last stored name or show the ID without failing the page. Use an opaque stable cursor for large file lists (sort value plus key); cap page size at 100. Avoid per-row main-DB lookups and test the chosen query plan with a large synthetic catalogue.

Expose authenticated, GET-only endpoints alongside `NativeCacheController`: `/api/native-cache/summary`, `/api/native-cache/files`, `/api/native-cache/activity`, `/api/native-cache/transfers`, and `/api/native-cache/evictions`. Define typed DTOs and OpenAPI contracts; validate filters, cursor, folder ID, sort field and page limit. Use response primitives that retain exact byte counts; frontend formatting should avoid unsafe JS integer arithmetic for values above `Number.MAX_SAFE_INTEGER`. Leave `/api/native-cache/entries` and `/ranges` compatible with the Settings inspector.

### 2. Time-window telemetry

Instrument hit bytes/count, miss bytes/count, and committed bytes at the existing cache read/write decision points. Aggregate in memory into UTC buckets and flush bounded batches to the local catalogue; do not write SQLite per block and do not put telemetry flushes on playback's critical path. Flush on graceful shutdown and report incomplete/unknown intervals after unclean restart. Keep `NativeCacheStatistics.Snapshot()` semantics intact for existing clients. Explicitly decide whether overflow-buffer reads count as native-cache hits (recommended: only verified store bytes count).

Add a bounded in-memory transfer registry keyed by active cache-fill operation. Start a transfer when work begins, update after durable block publication, remove it on completion/cancel/failure, and expose a snapshot. Cap tracked transfers and filename length; record drop/overflow count if the cap is reached. Background warming and playback fills should be labeled separately. Calculate speeds over a recent sampling window to avoid misleading lifetime averages.

### 3. Eviction events and correctness

Record reason and entry snapshot in the same catalogue transaction that removes a successfully unlinked live entry. Cover pressure, age, manual clear, repair/revision invalidation and folder retirement with explicit reason mapping; only use a reason supported by the actual code path. Examine every entry deletion path rather than assuming all deletions pass through `EvictCoreAsync`. Never log a deletion before payload removal succeeds, and do not expose stale cache data as playable if the NAS goes offline. Bounded retention should not block eviction or keep a payload alive. Existing historical evictions cannot be reconstructed and the UI should say history starts when the feature is enabled.

## Frontend delivery

Create `frontend/app/routes/native-cache/route.tsx` and small child components for the summary, folder table, transfer table, file browser/detail, and eviction table. Add the route to the existing navigation and link Manage Folders to Settings > Streaming's native-cache section. Reuse the existing `withUrlBase` proxy convention, loading/error components, formatting helpers and accessible table patterns. Keep dense tables comparable to the screenshots while matching InfiniDysk's theme. Search is debounced; filtering resets the cursor; stale responses cannot replace newer results. Provide keyboard access to rows/actions and text alternatives for coverage bars.

The detail panel should explain sparse cache semantics: a fully cached file has all verified ranges, a partial file may contain gaps, and a read across a gap falls back to Usenet while available. A percentage alone must not claim that a requested range is cached. A browse action must not trigger warming, source reads, scans, repair, or eviction.

## Implementation sequence and acceptance

1. **Read-only foundation:** catalogue aggregate and paged file/activity queries; API contracts; route shell; Overview folders and All Files. Verify existing Settings entry/range browser and pin action still work.
2. **Live telemetry:** rolling counters and active transfer registry; add Overview cards and active writes. Check cancellation, failed writes, partial commits, offline folders and restarts.
3. **Eviction history:** additive event table, all deletion paths, paged API and tab. Verify each actual reason and absence of events for failed/skipped deletion.
4. **Polish and validation:** responsive layout, filtering/sorting, accessibility, docs and release note. Use focused xUnit API/store tests and Vitest interaction tests, then the normal PR CI. Include a 100k-entry synthetic catalogue query timing check and confirm dashboard polling does not increase NAS payload I/O or perceptibly delay playback.

Acceptance cases: four mixed NAS folders, one offline folder, active fill with gaps, a pinned entry, read-only folder, partial and complete files, legacy entries without access counts, a repair invalidation, quota pressure, and a clear operation. Folder allocated totals must reconcile with retired debt; All Files count must reconcile with live entries only. Verify a real browser session against the LAN instance after an explicitly authorized deployment; do not infer production cache changes from screenshots or local tests.

## Deliberate limits

The first release is a browser and diagnostics surface. It does not add bulk eviction, arbitrary file deletion, cache import, or client VFS purges. Add those only with separate safety and interaction designs. Existing runtime counters cannot be backfilled into true 24-hour history; the UI must show the observation start until a full window has elapsed.
