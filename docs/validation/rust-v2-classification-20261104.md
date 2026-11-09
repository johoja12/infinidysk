# Rust Plex classification checkpoint — 2026-11-04

Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust baseline: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
Generated loopback data only. Production and the 1.x source remain unchanged.

## Contracts

- Default-off `imports.plex_matching` and optional `imports.plex_server_ids` govern
  classification. An absent selection means all enabled servers; `[]` means none.
  Typed validation, persistence, environment ownership and masked settings round trips
  are covered. Settings and Library expose sync status; Explorer exposes matches.
- Complete snapshots use migration 0015's separate single-row table. Failed paging,
  unreachable/inaccessible libraries and changed configuration retain the last full
  snapshot. Restored/outage snapshots are stale. Incompatible scopes are hidden.
- Matching uses every media Part path. Exact DAV/indexed local paths precede filename
  fallback. Fallback requires all candidates to agree on media and episode identity;
  ambiguity is visible. Responses cap at 100 matches and include the full count.
- Snapshot ceiling: 100,000 entries, 64 MB, 64 paths per entry, 4096 bytes per path/title,
  25-second fetch. A single blocking-build permit survives request cancellation until
  actual completion. Background sync waits for idle playback/imports, checks again
  between libraries and before publication, and refreshes daily. Explicit sync may
  run during playback. Empty selection/default-off make no Plex requests.
- No raw credentials or watched-state ownership are persisted in the snapshot. Scope
  contains a digest. Metadata classification does not warm media, authorize repair,
  establish deletion authority or prove a playback viewer.

## Qualification

The final frozen-source workspace run finished with **410 passed, one failed and six
ignored**, including actual PostgreSQL 17 and SQLite. Nine metadata cases cover
all-version matching, incomplete snapshot retention, stale/configuration changes,
ambiguity, default-off/empty selection, agreement fallback, response caps, foreground
yield and abandoned blocking work. Typed settings and database restart/persistence
checks pass. Workspace/all-target Clippy and formatting pass.

The failed test is the existing `a_stalled_job_does_not_hold_up_the_next_one` queue
case: its unchanged two-second deadline elapsed under the workspace run. It passes
in isolation with no timeout or product-code change. This is **not** a green full
suite, and the timing failure is not claimed fixed.

Both privileged kernel tests pass separately. All three original C# writer cache
checks pass separately. An initial attempt ran two original-writer suites in parallel;
one failed with the expected exclusive legacy ownership lock. Running them sequentially
passed. This harness scheduling error did not bypass or alter the lock. The external
local-sample NZB ignored case remains unrun.
The initial foreground-idle wiring failed compilation because two closures had been
moved into another worker. That was corrected before the qualification run.

API schema generation and TypeScript/Vite production build pass. The actual configured
app + Chromium checks pass: default-off sends no Plex requests; Settings enable/save;
exact indexed paths and year display; incomplete paging retains the previous stale
snapshot; ambiguous filenames are rejected; restored exact mappings win; empty
selection sends no requests; per-server selection survives reload; cache bytes remain
zero throughout metadata sync; Settings and details fit a 390px viewport. No page errors.

Canary binary SHA-256:
`cfa7214eb4243e611319fb2441b9a00836c7e4d2bafe294cba296145f2fc935f`.
Local receipt: `/tmp/infinidysk-rust-browser/plex-classification-validation.json`.

The browser harness separates actual generated local API/UI checks from simulated
UI-only environment-lock descriptors. It never logs into a real account, fetches
production media or measures live playback performance. Backend environment ownership
uses actual ConfigStore tests.

## Upgrade and remaining work

Migration 0015 is additive on SQLite and PostgreSQL. **Back up `/config` before
upgrading.** Matching remains optional, default-off and advanced-only. It does not
change a new installation's critical path or setup-wizard version. Legacy settings
and snapshot import are separate migration work and are not activated here.

Verified viewer correlation, large-root staged library scans, the full prefetch
source/budget/lead/policy/history governor, PAR2 execution/repair revisions, resumable
health sweeps and operational fork migration remain open. This remains a draft PR,
with no deployment or production cutover claim.
