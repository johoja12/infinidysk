# Exclusive v2 cache handover rehearsal

Prepared 2026-11-07 for [NAS qualification #238](https://github.com/johoja12/infinidysk/issues/238)
and [production migration #237](https://github.com/johoja12/infinidysk/issues/237).
This is a reviewable execution packet. No production handover has been executed.
Use the [migration plan](rust-v2-production-migration-plan.md) for the complete
application/database/mount transition and
[compatible writer report](validation/rust-v2-compatible-cache-writes-20261107.md)
for the storage protocol and limits.

The optional [eviction policy](validation/rust-v2-legacy-eviction-20261107.md)
remains disabled during initial handover. Its source requires a newly qualified
image; the earlier `b9220aa8` image predates eviction. If policy has ever been
enabled in a rehearsal, preserve the private Rust catalogue with the original
metadata and settle all `legacy_evictions_v1` intents and recency before handback.
Disabling policy alone does not settle an interrupted deletion.

## Already checked

The four registered payload roots are NFS. Read-only inspection matched the current
v1 catalogue shape and accounting trigger names. Same-host private probes outside
the cache roots accepted exclusive locks from read-only and read/write descriptors
and refused a second process on every mount. Probe files were removed. Original
cache owner files and payloads were untouched. These checks do not qualify NFS
server failover or ownership across independent hosts/containers.

## Required execution inputs

Record these in a private dated receipt, with no credentials or media names:

- Maintenance start/end and named operator; maximum rehearsal outage and rollback
  deadline; explicitly agreed latency/throughput/error thresholds.
- Exact committed v2 source, CI results, immutable image ID/digest and architecture;
  image labels must match source. Never use an unqualified moving tag.
- Current v1 image ID, service definitions, mount definitions and startup order.
- Every provider consumer, cache writer, scanner, warming/repair task and rclone
  process; include external hosts that can reach these NFS exports.
- Registered root IDs, canonical paths, device/inode and volume-marker hashes;
  owner-file device/inode; UID/GID, NFS options and shared-export capacity groups.
- Consistent database/config/blob/repair-ledger backup and tested restore receipt;
  original cache catalogue backup/checkpoint. These backups are metadata, not a
  copy of the cache payload. The metadata-only catalogue snapshot must be obtained
  after writers stop, not by copying a live WAL database file alone.
- A reviewed private trial configuration: separate v2 database/config, local v2
  metadata, original repair ledger immutable, legacy writes absent initially,
  background mutations/intake/providers disabled during read-only phase.
- A private canary item mapping with expected byte hashes at several offsets and
  across block boundaries, including sparse/chunked and smaller already-cached
  files. Avoid exposing file titles in receipts. Limit reads; do not scan 25 TB.

Do not start the live-cache phase without these inputs. Work on snapshot restores,
isolated fixtures and configuration review can proceed without an outage.

## Phase A: establish exclusive ownership and read-only reuse

1. Freeze intake and all enumerated mutators, then stop v1 and the old mount owner
   in the recorded order. Drain active consumers. Verify no other host owns the
   exports or performs eviction/scanning. Record clean shutdown/checkpoints.
2. Checkpoint original SQLite ledgers after shutdown and take the metadata backup.
   Verify original mount identities again. Never replace marker/owner files or
   adopt a new volume registration to make startup pass.
3. Start the exact trial image with providers, intake and mutators disabled, separate
   v2 state, original metadata/payloads bound read-only at their registered paths,
   and no `legacy.write`. Acquire every original owner lock. A simultaneous second
   trial owner must fail cleanly. If NFS requires it, use only the qualified writable
   bind of the exact original owner inode; do not create a new lock file.
4. Read bounded canaries through the shared VFS/WebDAV and the actual container
   FUSE mount. Verify all sampled bytes/hashes, seeks, original absolute link paths,
   smaller cached files and cache-hit counters. Missing data must defer with zero
   provider calls. Capture first-read and warm-repeat latency and throughput.
5. Exercise graceful restart and ownership release, then reacquisition. Check root,
   marker and owner identities. A mount/identity failure must refuse access and
   remain an uncertain retained-copy state. Perform disruptive NAS failure/remount
   tests only in an approved isolated environment; do not unmount active production
   exports as an ad hoc fault injection.
6. Stop the trial and hand ownership back to v1. Check bounded v1 reads and restore
   normal service before the rollback deadline if later phases are not approved.

Any hash mismatch, unowned writer, identity mismatch, lock failure, unaccounted
mutation or threshold breach stops the rehearsal. Preserve evidence; do not fix it
by deleting cache files, clearing journals or enabling providers to conceal misses.

## Phase B: isolated NAS write durability and performance

Before enabling production-root writes, use separately registered, clearly marked
generated fixture roots on each actual NAS export, outside the existing cache
roots, with private local catalogue/state. No original cache registration is edited.
Record allocation/quota/reserve baselines. Restrict provider input to deterministic
loopback data; do not consume production provider connections.

Qualify sparse/chunked sequential writes, concurrent playback plus warming,
priority fallback, quota/reserve refusal, cancellation, process death and restart.
Verify bytes and catalogue accounting after each case. Use the original C# reader
and scanner on these disposable roots to confirm rollback readability and journal
reconstruction. Repeat measurements with matched v1/v2 fixture geometry, concurrency,
cache state, network/mount settings and corpus; capture throughput, p50/p95 latency,
CPU/RSS and provider count. Keep cold and warm results separate. Test network loss,
remount and server recovery only with isolation and an agreed outage scope.

Keep the generated fixture inventory/receipts. Cleanup is limited to explicitly
owned generated roots after readers close; existing production payloads are never
cleanup targets. If any durability or performance gate fails, compatible writes
remain disabled. Local injected faults cannot substitute for this phase.

## Phase C: controlled compatible production-cache fill

This phase requires a reviewed successful A/B record and a separate production
write window. Stop v1 and reacquire all original locks. Preserve another consistent
metadata checkpoint. Enable `legacy.write` with explicitly reviewed folder IDs,
priorities, quotas, reserves and chunk size; mount only the required roots writable.
Keep automatic eviction/relocation absent and intake/background producers paused.

Allow one bounded canary missing block and one new generic source. Record the exact
first-write timestamp, source identity/generation, original folder, reservations,
committed block hashes and provider byte/connection budget. Verify all prior canary
bytes unchanged, restart without providers, and verify new committed data again.
Rehearse v1 byte readability after an exclusive handback. Format readability alone
does not establish application rollback after new v2 imports. Held pending states
require investigation and explicit recovery; never manufacture coverage.

## Production release decision

Keep all four tracking issues open until their acceptance evidence is reviewed.
The production migration issue additionally requires actual feature-gap decisions
(including Preflight), consistent copied application state and restore qualification,
real Plex/Arr/SAB/provider checks, final native mount/link verification, matched
performance acceptance, an agreed 24–48 hour soak, rollback capacity and a rehearsed
post-write import/history/link reconciliation procedure. Fill in the execution
record in the migration plan. Do not label local tests or basic NFS locks as a
successful production migration.
