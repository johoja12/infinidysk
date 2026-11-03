# Rust Plex and manual warming: local validation

Specification: fork C# `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust base: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
Branch: `feat/rust-v2-fork-migration`, draft PR #235. Production is unchanged.

## What this increment implements

- Bounded Plex transport, verified viewer credentials, PIN/Home authentication,
  private expiring connection handles, server discovery/testing, complete library
  paging, exact path mappings and optional Settings UI. Legacy valid account/server
  settings are carried over. Secrets are masked and support packs redact tokens.
- Durable manual warming jobs tied to source fingerprint and final-media ranges.
  Pause/resume/cancel, expiry, retry backoff and bounded history survive restart.
  Progress requires successful whole-block cache publication. Restart revalidates
  retained blocks; source changes supersede work.
- One strict background lane and a distinct `warm` provider workload. A waiting
  foreground read interrupts warming. Idle playback handles do not reserve provider
  work. No fillers, health-analysis attribution, playback sessions or speculative
  read-ahead are introduced by warming.
- Confirmed missing/corrupt slices report their exact flat metadata index to health.
  Inconclusive source failures request urgent checks without inventing missing slices.
  Failed/interrupted fills retain their durable allowance charge. Storage failures
  back off without marking source damage.

The complete 1.x fork port is still open: automatic Plex snapshots/planning, the full
wire-budget/viewer-lead governor, library catalogue parity, broader migration/operational
adapters, cache recovery/telemetry and PAR2 integration remain separate acceptance gates.
The allowance implemented here counts attempted final-media fill bytes, not NNTP wire
traffic. No performance percentage, live Plex/provider acceptance or deployment follows
from these results.

## Evidence layers

1. **Rust transport/service tests:** sixteen Plex crate tests and six private-handle
   service cases passed. Loopback responders exercise identity/authorization, bounds,
   redirects, complete paging, profile isolation, cancellation, managed settings and
   configuration races. These do not authenticate a real Plex account.
2. **Simulated Plex browser:** eight Chromium checks passed, with no page errors.
   The browser intercepted Plex and account responses. It checked sign-in/profile
   flow, PIN isolation, token-free selection, server test, exact mappings, masked
   round trip, environment locks and mobile width.
3. **Actual warming browser:** nine Chromium checks passed against the Rust HTTP API
   and generated local NNTP corpus, with no page errors. No API response was simulated.
   It covered pause-before-download, settings, admission/idempotence, complete warming,
   full-file/seek hashes, reload persistence, cached retry with no new source charge,
   and mobile width. This is byte-stream validation, not decoder acceptance.
4. **Queue integration:** ten cases cover restart/cache reuse, controls/allowance,
   source supersession, urgent repair, database failure, cancellation, foreground
   contention, stored-member offsets, encrypted plaintext/final tail, and exact slice
   reports. An initial exact-slice fixture discarded its loopback-server guard and
   therefore tested unavailability instead; retaining that guard corrected the fixture.
5. **Original cache interoperability:** three explicit tests passed again against
   the unmodified C# writer's generated sparse/chunked/stored/encrypted/partial fixtures.
   See [cache interoperability](rust-v2-cache-interop-20261103.md) for the copy-import
   and separate kernel FUSE evidence. Old roots are not shared writable with Rust.

Browser artifacts are retained locally in `/tmp/infinidysk-rust-browser/`:
`plex-validation.json`, `plex-mobile.png`, `prefetch-validation.json`, and
`prefetch-mobile.png`. Temporary canary mounts are stopped after validation.

## Reproduction

Use the pinned toolchain. To keep local validation storage bounded, this run used
`CARGO_INCREMENTAL=0`, `CARGO_PROFILE_DEV_DEBUG=0`, `CARGO_PROFILE_TEST_DEBUG=0` and `-j4`.

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -j4 -- -D warnings
cargo test --workspace --locked -j4 -- --test-threads=1
cargo test -p infinidysk-prefetch --locked -j4 -- --test-threads=1
cd ui
npm run api
npm run build
```

Set `INFINIDYSK_TEST_POSTGRES_URL` to a disposable local PostgreSQL administrator
connection before workspace tests; otherwise the optional PostgreSQL test skips its
actual database exercise. Never point the test at production. The original C# writer
fixture and explicit ignored-test commands are in the cache interoperability receipt.
The first metrics assertion expected only three workloads; it now verifies warming's
traffic separately. Failed runs are not included in successful results.

## Configuration and rollout

Plex is optional/default-empty. Warming is default-off and requires native caching.
Both remain advanced-only; no guided-setup/version change is needed. Environment
settings remain authoritative. The private job ledger uses the existing configuration
store and adds no schema migration in this increment. Earlier branch migrations are
additive; back up `/config` before an eventual upgrade. Preserve the separate 1.x
configuration/cache rollback path. This draft has no production cutover approval.

## Final qualification limits

The pre-slice-report checkpoint passed all **368 workspace tests**, with zero failures
and six intentionally ignored tests. The final full run passed **314 tests**, including
all ten warming cases and real PostgreSQL storage, then Cargo stopped on two unchanged
queue timing cases: `a_stalled_job_does_not_hold_up_the_next_one` and
`a_stuck_attempt_is_requeued_and_retried` (five ignored cases had been encountered).
The existing queue test binary had passed both in the earlier 368-test run. Its mtime
predates that run and it was reused for the failing run; queue/testkit sources have no
changes in this increment. Host load was about 33 on eight online CPUs. Reducing Tokio
workers did not reliably resolve the failures; the retry case subsequently passed in
isolation, while the stalled-job case still exceeded its two-second assertion.
**Do not describe the final workspace run as green or these failures as proven harmless.**
Their remaining scheduling/timing cause needs separate investigation before cutover.
No timeouts were relaxed and no cases were skipped to claim a passing full suite.
The subsequent focused stream/VFS run passed all **44 tests**, with zero failures.
The already-built final-source WebDAV binary passed its three HTTP cases separately,
including pointer deletion and retryable unavailable-provider responses.

The browser canary was built before the subsequent exact-slice reporting addition.
That addition is covered by the final warming integration cases. The local browser
artifact records its binary SHA-256, generated file SHA-256, size and this scope.
