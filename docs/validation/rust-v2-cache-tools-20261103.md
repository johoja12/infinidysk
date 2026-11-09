# Rust cache inspection checkpoint — 2026-11-03

Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust baseline: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
Local generated fixtures only. No production service, provider or cache was used.

## Results

- Final frozen-source workspace: **395 passed, zero failed, six ignored**. Actual
  PostgreSQL 17 runs alongside SQLite. All Rust doc builds pass. Workspace/all-targets
  Clippy, formatting, API schema generation and TypeScript/Vite build pass.
- All 17 native-store tests pass, including new probe pagination, confirmed checksum
  corruption, missing mount, changed mount identity, coverage retention on uncertainty,
  restoration, and persistent eviction cursors. The suite also checks leases, pins,
  quotas, read-only folders and concurrent commits.
- Both privileged kernel FUSE tests pass separately. Three original C# writer cache
  interoperability tests also pass using the generated `20261103c` fixture. The one
  local-sample NZB test remains unrun; it needs an external local sample.
- Application HTTP tests cover OpenAPI, authentication, missing repair intent and
  unavailable-cache responses. An unconfigured cache returns 503, not a fabricated
  empty result.
- A separate configured app and real FUSE mount serve an 8,388,608-byte generated
  fixture through WebDAV with a matching SHA-256. The actual admin routes return folder
  identity/free space, a one-block checksum probe with a continuation cursor, and
  exact persistent manual-eviction attribution. Evicting and refilling preserves the
  file hash. The same canary passes a full kernel-mount hash and three seek checks.
- Canary shutdown unmounts its filesystem. Only the task-owned PostgreSQL container
  is removed. Test receipts are local artifacts, not production acceptance evidence.

Earlier full runs hit the existing two-second stalled-queue deadline. An intermediate
run also had a doc-build mismatch because source changed during the run. Final checks
ran against frozen Rust source; neither failure was suppressed and no timeout changed.
The final passing run does not establish that the queue timing flake is fixed.

## Contracts and remaining scope

- `GET /api/v1/native-cache/evictions`: at most 200 rows, descending stable cursor;
  history survives restart and retains at most 10,000 events.
- `GET /api/v1/native-cache/diagnostics`: existing folder identity, read-only state,
  free bytes, committed bytes, invalid blocks and capacity settings. No payload scan,
  marker adoption or write test. API wait is bounded to five seconds.
- `POST /api/v1/native-cache/probe`: checks at most 16 committed blocks. A confirmed
  corrupt/missing payload on a verified volume loses coverage. An absent mount/marker,
  changed identity or timeout leaves coverage intact and retries the same cursor.
  API wait is bounded to ten seconds; actual blocking I/O retains its permit/lease.

Probes do not fetch provider data, rewrite payloads, reconstruct lost catalogues or
promote previously invalid blocks. Later verified fills restore coverage. The APIs
are administrative tools; dedicated tool UI, detailed traffic attribution and PAR2
repair-revision execution remain open. No new configuration or schema is introduced
here. Setup remains unchanged. Earlier additive migrations still require backing up
`/config` before an eventual upgrade. The original 1.x cache remains separate/read-only.

Reproduction uses the same environment and commands as the
[repair validation receipt](rust-v2-regrab-20261103.md), plus the ignored kernel and
original-writer fixture checks. Loopback canary artifacts are under a private
`/tmp/infinidysk-fork-local-*` directory. No live Plex, decoder, provider or controlled
performance qualification is implied. Library, full prefetch and operational migration
parity remain incomplete, so the PR remains a draft.
