# Rust Plex snapshots and automatic warming: local validation

Branch: `feat/rust-v2-fork-migration`, draft PR #235.
Specification: C# fork `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Upstream Rust base: `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
The C# source and production remain unchanged.

## Implemented

- Complete, bounded and persisted snapshots. Restore/outage marks results stale;
  incomplete passes never erase the last good result. Scope changes hide/discard
  obsolete results. Invalid saved formats warn and can recover.
- Verified viewer state for current/history-derived next episodes and partial movies;
  no owner fallback. Already-watched next candidates are filtered, malformed watched
  state fails safely, and mismatched playing variants are not guessed from the first
  metadata Part. Distinct viewing events permit rewatches without inflating episode
  evidence or accepting repeated pages.
- A newest-first 1000-event history window. A 50,000-event fixture requires ten page
  requests for the policy window. This is a request-count test, not a live benchmark.
- Exact DAV and opt-in local pointer mapping. The existing library pointer recognizer
  verifies current/former mount roots and signed current/retained STRM keys. Two I/O
  slots remain reserved through actual blocking completion, including abandoned waits.
- Source/configuration-bound automatic admission. Obsolete automatic jobs are cancelled,
  manual jobs and explicit cancellations survive, and evicted coverage requeues a
  completed intent. Changed source metadata cannot silently warm a replacement revision.
- Default-off settings and a preview with viewer attribution, stale/error state and
  authoritative environment locks. Previewing needs no native-cache/provider reads;
  actual downloading still requires global warming and the native cache.

The shared cache and 1.x copy adapter are unchanged. An integration case preloads
verified native blocks, runs automatic admission/warming, seeks through VFS, and
asserts zero NNTP requests. This preserves the cache layer used by 1.x copy import;
see [original writer interoperability](rust-v2-cache-interop-20261103.md).

## Results and scope

The focused application/library run passed **69 application tests and 20 Plex tests**.
The ten new planner cases cover automatic admission/warming, repeated intent identity,
outage/backoff/restart, unverified viewers, in-flight configuration changes, exact
source revisions, local opt-in/uncertainty, saved-format recovery, cancellation/eviction,
cache-only seeks and selected-version mismatches. The existing pointer-recognition
cases continue to cover signed STRM and known mount identities.

Workspace formatting and all-target Clippy passed. TypeScript/Vite build passed.
All three explicit original C# writer interoperability cases also passed again.
The full workspace run passed **329 tests**, including real local PostgreSQL and the
new planner/transport cases, then stopped on
`a_stalled_job_does_not_hold_up_the_next_one` at its unchanged two-second assertion.
Five intentionally ignored cases had been encountered before Cargo stopped. The retry
case that failed on an earlier checkpoint passed here. This is **not** a green full
workspace result. No timeout was relaxed and no case was skipped to claim one.

Chromium passed seven checks using the actual local Rust API: opt-in default, settings
save, nested-setting preservation, exact viewer-filter deduplication, snapshot refresh,
no warming side effects, and reload persistence. With no configured Plex servers, this
made no cloud calls. Four separate simulated checks passed: retained stale display,
viewer attribution, environment locking, and 390px layout. No page errors occurred.
The browser build preceded a final static source-lookup error wording refinement;
the ten planner tests passed again after that refinement.
Artifacts: `/tmp/infinidysk-rust-browser/predictions-validation.json` (including binary
SHA-256) and `predictions-mobile.png`. The first browser run used an exact label that
changed when the managed-setting icon appeared; the corrected selector verifies the
same disabled control. Earlier failing runs are not counted as passes.

An initial local-mapping test caught a missing opt-in guard; it was corrected before
this checkpoint. No live Plex account, production cache/configuration, real provider,
media decoder or deployment was used. The loopback backend tests and simulated UI
checks are separate evidence layers.

## Reproduction

Use the pinned toolchain and disposable databases only. This run bounded local build
resource use with `CARGO_INCREMENTAL=0`, `CARGO_PROFILE_DEV_DEBUG=0`,
`CARGO_PROFILE_TEST_DEBUG=0`, `TOKIO_WORKER_THREADS=4` and `-j3`.

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -j3 -- -D warnings
cargo test -p infinidysk -p infinidysk-plex -p infinidysk-prefetch --lib --locked -j3 -- --test-threads=1
cargo test --workspace --locked -j3 -- --test-threads=1
npm --prefix ui run api
npm --prefix ui run build
```

Set `INFINIDYSK_TEST_POSTGRES_URL` to a disposable local administrator connection to
exercise PostgreSQL. It must never point at production. Kernel mount acceptance and
original C# writer commands have their own cache validation receipt.

## Configuration and remaining work

No new database migration is added. The versioned snapshot uses the existing store;
old warming jobs deserialize as manual unless they carry automatic ownership. Settings
remain advanced-only/default-off, so the setup wizard/version stays unchanged. Back up
`/config` before any eventual upgrade; earlier branch migrations remain additive. Keep
the separate 1.x cache/configuration rollback path.

Library/hub catalogue policies, full viewer-lead/minimum/finish-watched/wire governor,
repair/regrab reconciliation, broader legacy migration and live performance acceptance
remain incomplete. These changes do not authorize merge or cutover.
