# Rust v2 upstream sync — 2026-11-13

## Source and preservation

Started on clean `main`, verified origin `johoja12/infinidysk` and upstream
`infinidysk/infinidysk`, then attempted
`git stash push --include-untracked -m 'codeman-daily-upstream-sync 2026-11-13'`.
There were no changes to save. Existing stash
`c367a0f038564a681ce7d0496de99fb7798b0382` remains intact.
Both remotes were fetched. Local main and origin/main matched at
`d92f57da2e0356e4ec1d5a6ce8d3d071f1018a3e`.

Upstream is the Rust rewrite: edition 2024 workspace, pinned Rust 1.99.1,
NNTP, shared VFS, native FUSE, and embedded React UI. Incoming range:
`01e1bba8be29d4fd921c159ca4c093014b307fa5..1ebecc81ec59b44c2e1d3a311cf54ba3ddb3c66e`
(12 commits):

- `f71df836`: share overlapping provider DNS lookups.
- `d3001788`: expose cgroup memory kinds, swap, process memory and logical cache size.
- `27b3a206`: separate import and health sampling.
- `6063ee6e`: document the temporary sqlx advisory exception.
- `4e1b17a5`: forget deleted-file health bookkeeping.
- `5a88f28a`: authenticated NZB inspection without queue/database publication.
- `52d4e4b0`: restructure README and use the lowercase product name.
- `419024bc`: synchronize memory-cache insertion with disabling/clearing.
- `8833c5db`: reduce large-import planning memory.
- `72b21186`: borrow segment metadata during health probes.
- `73258bee`: omit unused strict-read progress channels.
- `1ebecc81`: consume uploaded NZB buffers instead of copying them.

## Fork feature mapping and conflict decisions

The inventory and v1 history references remain in [fork migration](../fork-v2-migration.md)
and [fork parity audit](../rust-v2-fork-parity-audit-20261107.md).
The fork delta has 348 paths; 23 overlap incoming paths. An isolated worktree
was used to keep main unchanged until validation. No architecture was replaced
with legacy code and no whole-file ours/theirs conflict selection was used.

| Fork inventory | Candidate evidence |
| --- | --- |
| R01–R02 exact repair and original provenance | Arr repair, tracked repair and provenance implementations unchanged. |
| R03–R04 reader-safe urgent repair | Health reader-hold recovery, last-close wakeups, mutation/publication guards and urgent repair retained beside periodic deleted-item reconciliation. |
| R05 fair resumable sweep | DB sweep journal, health sweep and app content-sweep unchanged. |
| R06 durable guarded regrabs | `health/src/regrab.rs`, app regrabs and DB intent journal unchanged. The fork already replaced upstream's ephemeral `searching` map with durable outcomes; only inconclusive streaks need upstream's in-memory cleanup. Retained repair evidence is not deleted. |
| R07 bounded PAR2 recovery | Health PAR2 and app repair worker unchanged; geometry-checked NNTP BODY methods retained. |
| C01–C04 shared cache and original-format compatibility | Native-cache and VFS implementations unchanged. Stream memory toggling now uses upstream's synchronous toggle lock while retaining the fork's async repair-lineage guard and revision-stamped cache keys. |
| C05 cache administration | App native-cache API and cache UI unchanged. |
| P01–P05 Smart Prefetch and Plex predictions | Prefetch/Plex crates and executor/history unchanged. NNTP keeps warming workload, wire accounting, lane isolation, playback pressure and capacity reserve; strict reads retain their workload while omitting unused progress channels. |
| L01–L04 library catalogue and playback | Unified Plex/local Library, catalogue pagination, metadata, details, roots, filters and playback implementations unchanged. |
| M01–M04 state import and operational migration | Importer, migration acceptance/proof, DB migrations/blob versions and operation scripts unchanged. Owned NZB enqueueing preserves digest-bound migration admission paths. |
| S01 completed-link removal | DB/VFS durable removal and WebDAV regression retained; only upload buffer ownership changes. |
| S02 cancellation and multipart latency | Typed tickets, Drop cleanup, isolated lanes and shared stream retained. Inspection adds a lane-aware BODY method that does not remember misses. |
| S03 archive order and media readiness | Existing queue ordering/readiness logic retained inside upstream's shared import/inspection planner; sampling depth is passed explicitly. |
| O01 source provenance and observability | Fork image/source receipts, sessions and workload attribution retained; upstream memory breakdown is additive. |
| Fork size-based health presets | Health presets retained. Independent import presets and percentages added. Stored fork policies carry over on load; saving freezes import depth separately. Regression covers old custom/complete/deep policies, persistence, independent edits, and complete-proof routing. |

Eleven files conflicted. Other resolutions retain both test suites, both testkit
synchronization primitives, and both NNTP tag fields. The API schema is regenerated
from the combined Rust API, preserving fork routes and adding NZB inspection.
No material fork capability is removed. The replaced ephemeral search map is
superseded by the fork's existing durable regrab implementation.

## Validation

Rust 1.99.1 and Node 26.13.0 were used without changing version pins or lockfiles.
Locked UI dependencies were restored with `npm ci --include=optional` and an
isolated npm cache. Tests used a disposable PostgreSQL 17 fixture. Cargo debug
symbols and incremental artifacts were disabled locally to bound disk usage;
debug assertions remained enabled. Release settings retain fat LTO and one
codegen unit.

Passed:

- `cargo fmt --all --check`.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`.
- `cargo test --workspace --locked --no-fail-fast`: 963 passed, zero failed,
  13 ignored; PostgreSQL cases enabled.
- Focused sampling migration/routing tests, and all eight backup and sixteen
  configuration integration tests.
- `cargo deny check`: advisories, bans, licenses and sources passed.
- Final TypeScript/Vite build and generated OpenAPI schema.
- Final Playwright run: 114 generated-fixture checks across 360, 768 and 1280
  pixel viewports and both themes.
- Python migration tools: 27 passed.
- Four systemd templates and mocked announcement-script checks.

The first test run found two merge-caused regressions: eagerly materializing
an import preset wrote a default health row and broke a backup fixture's
explicit row insert. Materialize only migrated presets and intentional health
edits; untouched defaults remain absent. Both affected suites and the final
workspace suite passed after correction. A superseded overlapping build also
replaced an executable used by crash-recovery subprocess tests (`ENOENT` at
process spawn). No backup expectations were changed. The final full run used
stable test binaries and all crash cases passed.

`cargo build --release --locked` passed in 14m 45s on local Ubuntu 24.04
Linux x64 with the final embedded UI and the repository fat-LTO profile.
Native Ubuntu 26.04 x64/arm64 CI packaging remains a separate check.

Ignored cases require external original-writer/NAS/FUSE/sample fixtures, an explicit
throughput POC invocation, or are
subprocess/export helpers. Logs remain in the isolated worktree under
`scrap/sync-20261113/`.
These are local generated fixtures and builds. No deployment or live-provider,
Plex/Arr, NAS, production restore, macOS, or native arm64 acceptance is implied.
