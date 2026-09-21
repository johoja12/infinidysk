# Media Library Core Catalog (Read-Only) — Design

Date: 2026-09-21
Status: Draft for user review
Scope: Port only the core catalog browser from `/opt/projects/nzbdav` media library page into InfiniDysk. Read-only search. No repairs, deletion, cache actions, Plex sync, media analysis, Arr workflows, missing-episodes, or language audits.

## 1. Goal

Add an authenticated, read-only `/library` page to InfiniDysk that answers: "what media do I have, and which library symlinks point at it?" One deduplicated row per media item, with nested/expandable symlink mappings. Server-side search and pagination. Internal InfiniDysk files remain streamable through existing signed `/view` URLs; external-only links are inspection-only.

## 2. Non-goals (explicitly deferred)

- No repair/regrab, delete, ghost cleanup, cache warm/evict, Plex scan/metadata sync, ffprobe analysis, Arr history recovery, STRM backfill, or symlink migration.
- No Missing Episodes tab, no Wrong Languages tab.
- No writes to the library directory from this feature.
- No changes to queue/import lifecycle, WebDAV tree shape, or streaming auth.

## 3. Architecture

New dedicated route plus new read-only catalog API; existing `/explore` stays the raw virtual-filesystem browser.

```
Browser /library
  -> frontend loader (session auth)
  -> backendClient.listLibraryCatalog(query, filters, page, sort)
  -> POST /api/library/catalog (API-key auth, admin controllers pattern)
  -> LibraryCatalogService
       |-- DavDatabaseClient: DavItem files under /content (keyed by DavItem.Id)
       +-- LibraryMappingStore: persisted mapping index for media.library-dir
  -> response: deduplicated media rows + nested mappings + index freshness
```

- Internal mappings: symlinks whose target parses strictly as an InfiniDysk `/.ids/<DavItemId>` identity (no-follow read, syntactic parse only, never dereferenced through rclone/WebDAV mount).
- External mappings: all other library symlinks, recorded with link path, raw target text, size where obtainable without following, and broken/valid status.
- Rows: one per `DavItem.Id` for internal media (row size = the `DavItem` file size, not a sum over mappings); one record per external symlink link path when no `DavItem` identity exists (no dedupe across external links in this phase).
- Search server-side across item display name, content path, link path, and raw target. Sort deterministic (name, size, mapping count). Paginate all list responses; never use unbounded fetch.
- Catalog reads never scan the filesystem. A background hosted service owns discovery: initial scan after startup/backfill, then bounded periodic reconciliation. API returns `indexFreshness` (last successful scan timestamp plus nonfatal warning) so empty results are distinguishable from a stale/unavailable mount.
- Playback: internal rows reuse the existing `getDownloadKey` signed `/view` flow. External mappings expose no stream/download action.

## 4. Data model

Additive EF migration only; no changes to existing tables.

- `LibraryLinkMap` (new table): one row per discovered library symlink.
  - Key: stable row id; unique index on normalized link path.
  - `DavItemId` (nullable GUID; set only for verified internal `/.ids` targets).
  - `LinkPath` (normalized, rooted under `media.library-dir`; relative path is the identity used for the unique index and display, absolute form only for diagnostics), `TargetText` (raw symlink target), `MappingType` (internal/external), `Status` (valid/broken/unchecked/stale), `Size`, `LastSeenUtc`, `LastCheckedUtc`.
  - Broken rows are retained and displayed via an explicit broken/stale filter, never silently deleted by the scanner.
- `DavItem` remains filesystem truth and is queried read-only; no new columns on `DavItem` in this phase.
- SQLite and PostgreSQL mappings stay aligned (`DavDatabaseContext` / `PostgresDavDatabaseContext`).
- OpenAPI/admin contract overhead applies: catalog operation registered in `AdminApiContractCatalog`, generated contract JSON updated, frontend typed client method added, contract test extended.

## 5. UI

- New `Media Library` nav item beside `Files`, honoring the existing `NavFeatureId`/service-provider feature gating pattern.
- `/library` page using existing semantic daisyUI/Tailwind components (no bespoke stylesheet): debounced search input, mapping-type filter (all/internal/external/broken), deterministic sort control, server-side pagination.
- Table columns: display name, canonical content path or external target summary, size, mapping count, health/state badge. Expandable row lists every mapping: link path, target, type badge (internal/external), status (valid/broken), and for internal mappings an Open/Stream action via signed URL.
- Read-only affordance: page states no management actions exist in this phase; external rows labeled inspection-only.
- Freshness banner: shows last index time; non-blocking warning when scan is stale or mount unavailable.
- Visual mockup reviewed with user on LAN 2026-09-21 (table with first row expanded, internal + external + broken-external examples); user accepted layout.

## 6. Scanner and safety invariants

- Constrain all reads to configured `media.library-dir`; reject escapes.
- Use no-follow symlink APIs for type/target/size checks. Never call existence/size APIs that follow a link into the rclone/WebDAV mount (avoids self-deadlock/request storms per NZBDAV `LibraryLinkPolicy` lesson).
- Strict target parsing: only exact `/.ids/<DavItemId>` shaped targets become internal; everything else is external. Multi-level symlink chains are not followed: intermediate non-`/.ids` targets classify the link as external.
- Stale rows: links absent from a completed full scan are marked stale/unchecked and hidden from default results (retained for diagnostics, not silently deleted).
- Bounded concurrency and bounded per-run work; catalog API never triggers scans.
- Known mount/permission failures log as human-friendly single-line warnings with reason, no stack dumps; unexpected failures keep full stack. Follows repo stack-dump guidance.
- Auth: catalog controller uses the standard API-key/`FRONTEND_BACKEND_API_KEY` admin pattern; frontend route requires session. Do not copy the NZBDAV gap where auxiliary controllers lacked API-key enforcement.

## 7. Testing

- Backend: mapping parser unit tests (internal vs external vs malformed targets, escape rejection); scanner reconciliation tests (add/update/stale/broken retention); catalog query tests (dedupe by `DavItem.Id`, external-only records, search across name/path/target, pagination bounds); auth test for the new endpoint; contract test update.
- Frontend: loader/client test for search/filter/pagination params; expandable-row rendering test; no-management-actions assertion (no repair/delete/cache calls from this route).
- Docs: user-facing Media Library page doc with `since` pill for the introducing release; update docs nav.

## 8. Risks and mitigations

- Large flat libraries: mitigated by server-side search/pagination and persisted index; Explore's materialize-all-children pattern is not reused.
- Mutable paths: joins keyed by stable `DavItem.Id`; display paths are labels only.
- Plex filename-key join ambiguity from NZBDAV is not ported; no Plex enrichment in this phase.
- External files must never be treated as managed content: external rows carry no `DavItem` identity and no lifecycle actions.

## 9. Rollout

Single additive change set: migration + scanner service + catalog API + `/library` UI + contract/docs/tests. No setup-wizard change (no new user-facing setting; reuses `media.library-dir`). No restart-behavior change beyond standard hosted-service startup.
