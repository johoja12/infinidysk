# Fork-specific v2 parity audit

2026-11-07. Tracking: [audit #241](https://github.com/johoja12/infinidysk/issues/241),
[eviction #240](https://github.com/johoja12/infinidysk/issues/240), draft PR #235.
Scope: preserve features added or changed by our fork. Do not port unrelated
upstream-only v1 features as part of this work.

## Provenance and scope

Compared fork v1 `9c104b5961abe67c14e6565932e48ea340f1dbd3` with cached upstream/v1
`69fa8ae2fb809a0e849f864aeb76c86972e62815`. Their common ancestor is
`d29dda121bdd0a6e7601cd39db236c68f487b4d2`. The net backend/frontend/scripts fork
delta contains 368 paths. Compared those changes and all 24 capability rows in the
[original fork inventory](fork-v2-migration.md) against the current Rust tree,
settings/import mappings, workers, API/UI and tests. Rust baseline is `b9220aa8`
plus the opt-in eviction work delivered with this report.

An initial wider v1 surface sweep inspected 209 configuration keys and 100
controller files with route templates. That sweep identified upstream omissions,
but those are outside the clarified fork scope. Preflight, indexers/search/Prowlarr,
Stremio/profile adapters, Watchtower, Warden, upstream weekly schedules and generic
upstream metrics-history import have no net fork changes in the reviewed
subsystems. Their prematurely broad follow-ups #242–#246, #248 and #251 were
closed as not planned. This does not claim these upstream features exist in v2.
Existing production configuration/consumer dispositions remain part of #237.

Private source inventories are retained under
`scrap/upstream-sync-20261107/eviction-parity/`. They contain source names, route
templates and configuration key names, not production values. Route counts are not
a parity score: v2 intentionally has a new admin API.

## Confirmed fork follow-ups

| Finding | Source evidence and current behavior | Tracking |
| --- | --- | --- |
| Safe v1-format eviction and recency | Fork `Services/NativeCache/*` adds the original cache, eviction and access accounting. The Rust bridge lacked eviction and did not refresh original access buckets. This change adds explicit opt-in age/pressure eviction, active-session admission, pins, asynchronous recency and durable deletion recovery. | [#240](https://github.com/johoja12/infinidysk/issues/240); [qualification](validation/rust-v2-legacy-eviction-20261107.md). Local tests do not qualify production NAS. |
| Legacy cache administration/observability | Fork `NativeCacheBrowser*` and cache controllers support inventory and management. Rust legacy hits/writes/coverage work, but file lists, byte totals, manual pin/unpin/exact-entry actions and physical I/O diagnostics still describe the Rust store. | [#249](https://github.com/johoja12/infinidysk/issues/249). Do not interpret zero native totals as an empty legacy cache. |
| Legacy reconciliation and lifecycle | Fork scanner/checkpointer/reclamation and health-protection maintenance have no complete Rust equivalent for the original format. Matching pending-block readback and owned eviction recovery work. Pre-existing v1 retired copies, journal compaction, allocation reconciliation and stale automatic legacy-pin cleanup remain separate. | [#250](https://github.com/johoja12/infinidysk/issues/250). Preserve manual pin 1; do not clear unknown holds or delete unowned data. |
| Failed-import recovery visibility | `WatchdogRecoveryService.cs` is a 107-line fork addition. The fork controller/UI adds confirmed replacement identity/import time for failed imports. Rust regrab inspection is item/intent-centric; no equivalent general failed-queue recovery view was found for imports that produced no managed item or regrab intent. | [#247](https://github.com/johoja12/infinidysk/issues/247), narrowed to this fork addition. It does not include upstream Watchdog candidate/stall failover. |

## Coverage of the fork inventory beyond cache

“Implemented” below means a corresponding outcome and local evidence exist. It
does not certify every v1 edge case or production deployment. Archived state is
not active behavior. Live integration, real-data restore and matched performance
remain separate gates in #237/#238.

| Original rows / fork outcome | Rust evidence / status | Remaining work |
| --- | --- | --- |
| R01–R02: missing-grab repair and exact original-download recovery | `crates/arr/src/repair.rs`, scoped history/owner resolution and ambiguity/failure tests. Implemented. | Actual configured Arr versions, histories and mutations: #237. |
| R03: reader-safe repair, last-close wakeup and confirmed-damage priority | `crates/vfs/src/session.rs`, health admission/reader holds, repair barriers and cancellation tests. Implemented. | Actual FUSE/WebDAV clients and mutation timing: #237. |
| R04: urgent playback/warming damage across provider outages | Health/prefetch fault submission and durable retry/priority propagation. Implemented. | Live provider circuits, budgets and representative damaged files: #237. |
| R05: resumable sampled content sweep and fair rechecks | App sweep and health worker preserve cursor, quotas, pause/resume and source revision. Implemented. | Production schedules, source distribution and fair load acceptance: #237. |
| R06: regrab intents, guarded link removal and detached observation | `crates/health`, `crates/infinidysk/src/regrabs.rs`, library removal journals and tests. Implemented for item/regrab intents. | General failed-import recovery visibility is the narrower #247 gap. Remote restart/readback qualification: #237. |
| R07: bounded PAR2 recovery/publication | `crates/par2`, `crates/health/src/par2`, repair lineage and original-post discovery. Implemented with documented budgets/ceilings. | Real providers, representative damage/encryption, memory/read budgets and decoder/playback acceptance: #237. Small fixtures do not prove every damage envelope. |
| C01: verified final-media cache and v1 reuse | `crates/native-cache`, shared VFS/stream bridge and original C# interoperability fixtures. Implemented reads/fills without payload conversion/copying. | Actual NAS handover/failure/performance: #238. |
| C02: ownership, folders, quotas, watermarks, eviction and pins | Exclusive ownership/volume checks, multiple folders, quota/reserve guards and automatic health pins existed. #240 adds policy eviction and recency. | Legacy manual actions #249; lifecycle #250; real device/NAS contention #238. |
| C03: repair revisions, multipart identity and invalidation | Immutable import provenance/repair ledger, generation-bound keys, repair publication witnesses and reader-safe invalidation. Implemented. | Actual source/repair handover and rollback: #237. |
| C04: progressive cold reads, fill coalescing and source-window reuse | `crates/stream` and native-cache admission/cancellation tests. Implemented. | Matched cold/warm first-byte/seek/throughput under playback plus warming/eviction: #238/#237. |
| C05: cache browser, ranges, history and I/O diagnostics | Rust-store UI/API and combined legacy coverage/hit accounting exist. | Legacy-store administration/physical diagnostics #249; do not claim complete browser parity. |
| L01: persistent library/link catalogue and extra roots | `library.rs`, `catalogue.rs`, library scan/configuration modules and durable tests. Implemented. | Real paths, permissions, filters, external links and full scan/mutation acceptance: #237. |
| L02: Plex metadata classification and scoped matching | `library/metadata.rs`, `crates/plex`, explicit server/library policies and tests. Implemented. | Actual Plex APIs, path mappings, ambiguity and large libraries: #237. |
| L03: file details, health, ranges and regrab controls | Library detail/regrab APIs and UI use current source, health and combined cache coverage. Implemented outcomes. | Legacy manual cache management #249; actual interaction acceptance #237. |
| L04: grouped playback, viewer attribution and cache-served accounting | VFS sessions/traces, Plex source-session correlation and current source/time/scope guards. Implemented; ambiguity remains explicit. | Do not assign every HTTP/FUSE read to a remote viewer. Real multi-client verification: #237. |
| M01: preserve fork state/settings and ownership | Importer and `upgrade/fork.rs` map selected fork settings; Plex accounts/servers and Smart Prefetch policy mappings exist. Unknown settings/journals are privately archived. | Consistent cross-store real-data restore and explicit pending-work dispositions: #237. Preservation is not replay/activation. |
| M02: legacy NZB catalogue/mapped exports and canary | Rust digest-bound recovery/export, validation and isolated canary tooling exist. | Real source identity, receipts, permission and no-duplicate acceptance: #237. |
| M03: independent import/validation lanes and failed-import operations | Durable migration submission/readback, frozen schedules and terminal dispositions exist. | Actual failed/pending batches and remote mutation acceptance #237; generic queue recovery view #247. |
| M04: runner/systemd/Plex priority ordering | Rust runner, guarded service/timer templates and sealed priority snapshot/resume machinery exist. | Real runner/host/provider budgets and startup ordering: #237. |
| S01: completed-link removal stays removed | DB/VFS/WebDAV/FUSE regression coverage and durable removal records. Implemented. | Actual consumer bindings and rollback reconciliation #237. |
| S02: cancelled transport cleanup and multipart latency | Ordered NNTP completion, permits, streaming cancellation and source-window tests. Implemented. | Measured contention/latency on real hardware, not just local fixtures: #237/#238. |
| S03: media readiness and archive ordering | Queue/archive checks and malformed/reversed/obfuscated stored-member tests. Implemented. | Actual representative media decoding and limits #237. |
| O01: exact source/image provenance | Fork builder, source/submodule receipts and repository-isolated release metadata exist. | Previous `b9220aa8` image predates this eviction change. A new image, remote CI, arm64 and actual production gates are still required. |
| Fork Prometheus/observability additions | Rust live/persistent metrics, Prometheus and shared session/cache-hit attribution exist. | Legacy physical I/O/totals #249, external scrape/dashboard contracts and measured values #237. Generic upstream history import is excluded. |
| External consumers of fork APIs/mounts | Deployment dependency, not proved by module presence. | Inventory actual Plex preview/BIF generators, old API/RC callers, import runners and consumers where deployed. Verify real execution/output against v2 in #237. No external service was changed by this audit. |

## Release boundary

This is a fork feature review, not an assertion of complete upstream-v1 product
parity. Open implementation follow-ups are #247, #249 and #250. Eviction #240 is
implemented locally with policy disabled unless explicitly configured. Device
contention, real NAS crashes/remounts, live Plex/Arr/provider behavior and migration
restore/rollback remain acceptance gates. The original per-device writer gate is
an outcome reference for fairness, not a requirement to copy C# locking mechanics.

Keep eviction disabled during initial cache reuse and migration. Test policy
reclamation on generated NAS data before any separately reviewed production
configuration. Preserve existing cache storage/payloads in place. No production
services, configuration, database or cache payload changes occurred in this work.

## 2026-11-09 UI correction

The earlier outcome inventory did not prove complete UI parity. The original cache
inventory was absent from the browser and the fork workflows lacked dedicated
navigation. See [UI qualification](validation/fork-ui-parity-20261109.md) for the
native cache, Plex Library and Smart Prefetch workflows, generated browser tests
and separate live Plex protocol evidence. Treat those complete user flows, not
module/component presence, as the UI acceptance boundary. Lifecycle/reconciliation
#250 remains a separate implementation scope.
