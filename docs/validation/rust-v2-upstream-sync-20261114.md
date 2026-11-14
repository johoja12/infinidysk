# Rust v2 upstream sync — 2026-11-14

## Source and preservation

Started on `main` with origin `johoja12/infinidysk` and upstream
`infinidysk/infinidysk`. The dated `git stash push --include-untracked` attempt
reported no local changes; no new stash was created. Existing stashes, including
`c367a0f038564a681ce7d0496de99fb7798b0382`, remain intact.
Both remotes were fetched. Main and origin/main matched at
`1f123e2d8439dd5d1ba7792669c82540ea9b6a86`; no fast-forward was needed.
An isolated detached worktree protects the shared checkout during validation.

Upstream is the Rust v2 workspace: edition 2024, Rust 1.99.1, shared VFS,
NNTP, native mount and embedded React UI. Incoming range:
`1ebecc81ec59b44c2e1d3a311cf54ba3ddb3c66e..53ba6cc5a2b85afa898ae3902f04ce89186c8d22`
(eight commits):

- `2d285883`: keep FUSE serving after kernel errors and clean up at shutdown.
- `f8323e0f`: release 2.0.0-beta.9.
- `a5b33db6`: skip analysis requests for deleted files, including PostgreSQL races.
- `a2484405`: reuse analysis data for library scans and playback.
- `7bd951c5`: update the CodeQL action.
- `60f27447`: update locked flate2 and toml dependencies.
- `36f93fec`: request full CodeRabbit re-reviews.
- `53ba6cc5`: expose playback startup stages in traces and support packs.

## Fork feature mapping and conflict decisions

The capability specification remains [fork migration](../fork-v2-migration.md)
and the [fork parity audit](../rust-v2-fork-parity-audit-20261107.md).
The common-ancestor comparison finds 350 fork-specific paths and 35 incoming
paths, with 21 overlaps. Of the fork-specific paths outside those overlaps,
328 are unchanged; the additional change is the cache repair regression.
The upstream mount rename carries the fork mount behavior and native-cache test
support into `crates/mount`. No capability is declared superseded.

| Inventory | Rust implementation and result |
| --- | --- |
| R01–R02 exact repair and original-download provenance | Arr repair/tracked history and provenance unchanged. |
| R03–R04 reader-safe urgent repairs | VFS repair admission, article publication guards, source binding and last-close wakeups retained alongside FUSE request tracing. Health urgency and durable holds unchanged. |
| R05 fair resumable sweep | App content sweep and durable DB sweep state unchanged. |
| R06 guarded regrabs | App regrabs, durable journals and tracked Arr repair unchanged. The prior fork replacement of the ephemeral searching map remains; the deleted-item test retains the new analysis assertions without resurrecting that map. |
| R07 bounded PAR2 repair | Repair publication, revision identity, PAR2 workers and DB repair evidence unchanged. Shared cache requests retain revision-stamped keys, geometry checks and bounded revision retries. |
| C01–C04 global cache and original-format compatibility | Native-cache/disk/lineage/revision code retained. Analysis now warms the shared segment cache using health priority; playback does not join a background provider flight. Warming keeps its separate retained-memory lookup and provider admission path. Original-format compatibility and ownership unchanged. |
| C05 cache administration | Cache APIs, inventory, policy and UI unchanged. |
| P01–P05 Smart Prefetch and Plex | Prefetch/Plex crates, executor, history, lane isolation, warming wire grants and spare-capacity reserve unchanged. Warming byte/request metrics retained alongside new trace timing. |
| L01–L04 library and playback | Unified Plex/local library, pagination, metadata, details and recent playback/native-cache attribution retained. The latest Plex sync deadline fix remains. New pending FUSE requests and startup timing are additive. |
| M01–M04 migration and operations | Importer, compatibility bridge, schemas, blob layouts, digest-bound acceptance/proof and operations scripts unchanged. |
| S01 durable completed-link removal | DB/VFS journal and async durable FUSE removal retained. New mount transport and cleanup do not replace link removal semantics. |
| S02 cancellation and multipart latency | Typed tickets, Drop cleanup, lane admission and revision readers retained. Background cache flight replacement uses a generation guard so an old flight cannot remove a new playback flight. |
| S03 archive readiness/order | Queue/archive code and coverage unchanged. |
| O01 source provenance and observability | Fork build/source receipts, support redaction including Plex credentials and session attribution retained. Upstream support-pack trace diagnostics are additive. |
| Fork sampling policies | Existing migrated size-based health/import presets and independent configuration retained. |

Nine files conflicted. Resolutions combine both test sets, keep the native-cache
package while adopting upstream's mount crate rename and external FUSE transport,
and carry both workload and repair revision through shared cache flights.
No whole-file ours/theirs selection was used.

Regression coverage starts a gated health-priority original fetch, publishes a
repair and verifies playback receives the new revision without waiting for or
reusing old data. Existing tests cover analysis cache reuse, background/playback
flight replacement and cleanup, strict warming retry/admission, durable link
removal and reader-safe repair. New trace tests use the fork's optional session
admission result. The mount open path moves its file immediately into an Arc,
retaining repair EAGAIN. Clippy still reports the moved value as a local
resource; a narrowly scoped lint annotation documents this ownership proof.

## Validation

Logs are under
`scrap/sync-20261114/` in the isolated worktree. Rust 1.99.1 and Node 26.13.0
are used without changing version pins. Locked UI dependencies were restored
with optional dependencies and an isolated npm cache. A disposable PostgreSQL 17
fixture enables PostgreSQL cases. Local debug symbols and incremental artifacts
are disabled to bound disk use; debug assertions remain enabled.

Local fixtures and builds do not establish remote CI, native Ubuntu 26.04
x64/arm64 packaging, macOS, live providers, Plex/Arr, NAS or production acceptance.

Passed gates:

- Formatting and strict workspace/all-target Clippy passed.
- Final workspace run: 978 passed, zero failed, 13 ignored, PostgreSQL enabled.
  The initial full run passed with the same totals.
- Strengthened health-flight/repair/playback regression: passed.
- Cargo deny: advisories, bans, licenses and sources passed under repository policy.
- Combined OpenAPI schema regenerated and compared; the only generator-only
  difference was a random provider default UUID, which was kept stable.
- Final UI TypeScript/Vite build passed; 114 browser fixture checks passed
  across three widths and both themes.
- Python migration operations: 27 passed.
- Four systemd templates and mocked announcement checks passed.

`cargo build --release --locked` passed in 12m 30s on local Ubuntu 24.04
Linux x64 with the final embedded UI and repository fat-LTO profile. The initial
release attempt was superseded while finalizing the lint annotation and UI build;
the completed build used the final source.

Ignored tests require external
original-writer/NAS/FUSE/sample fixtures, explicit throughput POC invocation, or
serve as subprocess/export helpers.
