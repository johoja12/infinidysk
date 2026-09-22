# Media Library File Modal Design

Date: 2026-09-22
Status: Approved by user (scope A: modal + one new details endpoint)
Related: `docs/superpowers/specs/2026-09-21-media-library-core-catalog-design.md`,
`docs/superpowers/plans/2026-09-21-media-library-core-catalog.md`

## Goal

Refresh the InfiniDysk `/library` page so catalog rows are clickable and open a
file modal in the style of NzbDAV `/media-library`, reusing InfiniDysk backends.
NzbDAV-only tooling (ffmpeg verify, analyze, zero-pad, cache verify, per-file
Plex scan, delete, STRM/missing-episode tooling) is explicitly out of scope
because InfiniDysk has no backend equivalents.

## Row interaction

- Catalog rows become clickable with button semantics: keyboard operable
  (Enter/Space), visible focus ring, `aria-haspopup="dialog"`.
- Click opens the modal. Internal rows fetch per-file details; external rows
  show a mappings-only modal with no action bar.
- The existing expandable mappings row stays as the no-JS fallback.
- Pagination, search, type filter, and sort behavior are unchanged.

## Modal sections

- Header: display name + health badge.
- File facts: content path, size, health, index freshness.
- Actions:
  - Preview: existing Explore `MediaPreview` player with the signed `/view` URL.
  - Download: signed URL with `download=true`.
  - Run health check: `POST /api/trigger-health-check`; whole-queue semantics
    must be disclosed in the UI (it queues all due checks, not just this file).
  - Requeue repair: `POST /api/requeue-action-needed-health-checks?davItemId=`;
    a 409 surfaces the repair-disabled reason verbatim.
  - Prewarm: `POST /api/prefetch/operations` (`warm`); hidden with an
    explanation when Native cache is inactive.
- Mappings list sourced from the catalog index (internal + external targets).
- Health history: latest `HealthCheckResult` for the item with a link to
  `/health`; an explicit empty state when there is none.

## Backend (two additive changes, no migration)

1. New `GET /api/get-library-file-details?davItemId=` returning DavItem fields
   (id, name, path, size, release date, last/next health-check dates,
   repair-pending flag, history id), the latest `HealthCheckResult`, and index
   mappings. `400` on invalid id, `404` on missing item, API-key auth like the
   catalog endpoint.
2. Optional `davItemId` filter (validated UUID) on
   `GET /api/get-health-check-history`, composable with existing filters.
3. Admin contract version bump, `scripts/export-admin-openapi.sh` regen, JSON
   schemas for the new endpoint. No database migration.

## Errors, testing, docs

- Action failures surface as inline `Alert` feedback reusing health-route copy;
  the modal never closes on failure.
- Tests: endpoint auth/validation/shape tests, history-filter test, client
  method test, modal open/actions/empty-state tests; existing suites unchanged.
- Docs: `docs/features/media-library.md` gains a modal + actions section with
  a `since` pill.

## Non-goals

- No ffmpeg/analyze/zero-pad/cache-verify/Plex-scan/delete actions.
- No missing-episodes, wrong-languages, ghost-file, or bulk-recovery tooling.
- No changes to catalog search/sort/pagination semantics.
