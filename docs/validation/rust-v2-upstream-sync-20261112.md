# Rust v2 upstream sync — 2026-11-12

## Source and preservation

Verified branch `main`, origin `johoja12/infinidysk`, and upstream
`infinidysk/infinidysk` before mutation. The named stash
`codeman-daily-upstream-sync 2026-11-12` preserves tracked and untracked local
changes at `c367a0f038564a681ce7d0496de99fb7798b0382`. It remains intact.
The working tree was clean after stashing. Both remotes were fetched.
Fork main and origin/main matched at `0340202809f95d040731a83cf796be3cc07457e8`.

Upstream remains the Rust rewrite: edition 2024 workspace, Rust 1.99.1,
shared VFS, NNTP, native FUSE and embedded React UI. The incoming range is
`c1f2b40d2ac4e2f7f3e1f1370221428c1573aacb..01e1bba8be29d4fd921c159ca4c093014b307fa5`
(nine commits):

- `6874a08c`: retry server-ended NNTP sessions on a new connection.
- `2aef6a6b`: restore WebDAV response buffering.
- `766acc2d`: classify dropped Arr requests as transient.
- `d1bb0afe`: bound NNTP login and distinguish provider stalls from local admission waits.
- `4ab40fcb`: try alternate provider addresses within the connect deadline.
- `6cd716ae`: extend whole-library Arr requests to three minutes.
- `35609ed9`: release 2.0.0-beta.8.
- `b860954c`: load detailed agent rules on demand.
- `01e1bba8`: keep long output and duplicate rule loads out of context.

## Fork feature mapping

The capability inventory and original v1 code/history references remain in
[fork migration](../fork-v2-migration.md) and
[fork parity audit](../rust-v2-fork-parity-audit-20261107.md).
This pass compared the fork delta from the common ancestor with the incoming
paths: 205 fork-specific crate files, of which nine overlap incoming changes.
All other fork-specific crate files are unchanged by this merge. These mappings
were checked against the candidate before completing the merge:

| Inventory | Rust implementation and merge result |
| --- | --- |
| R01–R02: exact repair and original-download provenance | `arr/src/repair.rs` and `repair/tracked.rs` unchanged. Tracked exports and uncertain/checkpoint transient classification retained alongside upstream dropped-connection classification. |
| R03–R04: reader-safe urgent repairs | VFS sessions, health admission and app repair unchanged. Retryable WebDAV repair exclusion retained. |
| R05: resumable fair sweep | App `content_sweep.rs` and health/DB sweep state unchanged. |
| R06: guarded regrabs and durable outcomes | App `regrabs.rs`, tracked Arr repair and DB journal unchanged. Upstream extends only whole-library request timeouts; mutation timeouts remain unchanged. |
| R07: bounded PAR2 recovery | App repair worker, health discovery, PAR2 verification and repair unchanged. |
| C01–C04: shared final-media cache, source revisions, progressive reads and original-format compatibility | Native-cache, stream cache/disk/lineage and VFS native/revision implementations unchanged. Original payload reuse, ownership, pins and reader guards retained. |
| C05: cache administration | App native-cache API and cache browser/settings UI unchanged, including original-cache history and diagnostics. |
| P01–P05: Smart Prefetch, Plex authentication/predictions, priority and attribution | Prefetch/Plex crates and app executor/history unchanged. NNTP keeps typed warming workload, wire grants, capacity reserve and lane isolation. Deadline and reconnect changes carry the original lane through worker selection and opening. |
| L01–L04: library catalogue, metadata, details and playback | App library/Plex and VFS session code unchanged, including compact paginated catalogue reads and restored UI workflows. |
| M01–M04: fork state import, mapped packages, durable submissions and operations | Fork importer, upgrade/fork, migration/proof, DB schemas and operation scripts unchanged. No existing migration or blob layout changed. |
| S01: persistent completed-link removal | DB removal records and VFS removal logic unchanged. WebDAV keeps async durable removal and its regression test. |
| S02: cancellation and multipart latency | Fork ticket/Drop accounting, lanes, warming wire budget and stream cancellation retained. Upstream restores its response buffer without replacing the shared cache or native source windows. |
| S03: media readiness and archive order | Queue/archive implementations and regression coverage unchanged. |
| O01 and observability | `scripts/operations/build-fork-image.py`, binary source receipt and fork metrics/session attribution unchanged. |

No fork capability is declared superseded by these nine upstream commits.
Existing live-provider, Plex/Arr, NAS, rollback and performance qualification
limits remain separate from this sync. No production data or service is changed.

## Conflict decisions

Four files conflicted. NNTP client tests retain warming wire accounting and add
upstream stalled-login coverage. Connection tests keep both wire-refill cleanup
and upstream address/debt tests. Pool admission keeps warming wire readiness,
lane isolation and original request tickets, while adding upstream admission
budgets, bounded connect/login, alternate addresses and 400 reconnects.
WebDAV tests retain durable completed-link removal and the buffered range test.
No whole-file ours/theirs selection was used.

The reconnect regression now exercises both playback and warming, verifies
permit release and proves warming retries do not become playback attempts.

## Validation

Toolchains are pinned
Rust 1.99.1 and Node 26.13.0. Locked UI dependencies were restored with
`npm ci --include=optional` and an isolated npm cache. Cargo reuses the existing
local target with incremental caching and debug symbols disabled to bound disk
use; debug assertions remain enabled. PostgreSQL tests use a new disposable
PostgreSQL 17 fixture with an explicit fixture password.

Passed gates:

- `cargo fmt --all --check`.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`, including the final reconnect regression.
- `cargo test --workspace --locked --no-fail-fast`: 917 passed, zero failed, 12 ignored; PostgreSQL cases enabled.
- Focused reconnect regression: passed for playback and warming.
- `cargo deny check`: advisories, bans, licenses and sources passed.
- `cargo build --release --locked`: local Linux x64 release build passed with the repository fat-LTO profile (14 minutes).
- Node 26 UI TypeScript/Vite build: passed; existing large-chunk warning remains.
- Generated browser fixtures: 84 passed across 360, 768 and 1280 pixel layouts and both themes.
- Python migration operations: 27 passed.
- Four systemd templates: verification passed.
- Mocked announcement-script checks: passed.

Ignored Rust cases require FUSE/kernel access, original C# writer or NAS fixtures,
local sample inputs, or are subprocess/export helpers. Browser results are local
generated fixtures. Remote CI, native arm64 packaging, live providers and
production acceptance are not established by these local checks.
