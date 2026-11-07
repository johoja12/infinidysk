# Production migration plan: fork 1.x to fork 2.x

Prepared 2026-11-07. Status: **planning complete; production migration blocked on
the acceptance gates below**. No deployment, production configuration change,
service restart, cache copy or database migration was performed for this plan.

**Required storage policy:** reuse the existing cache storage and payloads in
place. Do not copy, move, remove, truncate, rewrite or reformat the existing
cache as part of migration. Stop v1 before handing cache ownership to v2.
Concurrent writable ownership is forbidden. Config/database backups remain
required; they are distinct from copying cache payloads.

**Implementation update:** an explicit read-only legacy adapter now serves
verified v1 payloads in place through the shared playback/warming lookup.
See [compatibility validation and setup](validation/rust-v2-in-place-cache-20261107.md).
Actual production NAS handover, performance and rollback still need qualification.
Opt-in compatible writes are now implemented locally; see the
[writer qualification report](validation/rust-v2-compatible-cache-writes-20261107.md)
and [NAS handover runbook](rust-v2-cache-handover-runbook.md). Actual live writes
remain gated on that rehearsal. A cold-cache
start or verified-copy migration is not an accepted fallback.

The [fork-specific audit](rust-v2-fork-parity-audit-20261107.md) is the current
feature follow-up scope. Do not turn unchanged upstream-only v1 omissions into
new fork port requirements. Existing production configuration/consumer changes
still need disposition. [Optional policy eviction](validation/rust-v2-legacy-eviction-20261107.md)
is a separate management feature: keep it disabled during initial migration,
include its private recovery ledger in backups, and settle intents before handback.

## Source and upstream sync

After fetching both remotes, `feat/rust-v2-fork-migration` and its origin branch
both pointed to `0c577cc1c8bede5116b8ff5ac8b83b6a9de4d3ad`. Latest upstream Rust
`main`, `e3e0507366d9e17abda275018644d3aa92f069f2`, was already an ancestor.
`git rev-list --count HEAD..upstream/main` returned zero. Sync required no merge.

That revision is the pre-adapter validation baseline. It cannot exercise the new
in-place adapter. Build and validate an exact image from the commit containing
the adapter before any real-storage rehearsal. The
[local acceptance report](validation/rust-v2-acceptance-0c577cc1-20261107.md)
records generated-data, original C# writer, kernel FUSE and release-image checks.
It does not qualify copied production data, live integrations or production
performance. Later source changes require an updated source/image receipt and
appropriate revalidation. A documentation commit does not change that image.

The existing draft is [PR #235](https://github.com/johoja12/infinidysk/pull/235).
Do not merge it or replace production as part of this planning task.

## Verified production baseline

Read-only observations on `nuc-1` on 2026-11-07; refresh before rehearsal or cutover.
Sizes are decimal bytes. Counts from PostgreSQL statistics are estimates.

| Item | Observed state | Migration consequence |
| --- | --- | --- |
| Application | `local/infinidysk:9c104b59`, healthy, zero restarts | Retain this image and original service definition for rollback. |
| PostgreSQL | `infinidysk-postgres`, PostgreSQL 17.11; database 366,835,379 bytes | Use a separate empty v2 database and storage. Never migrate into the live v1 database. |
| Database estimates | 149,884 DavItems; one QueueItems row; 23,740 HistoryItems; 616 ArrRegrabRequests | Obtain exact counts and explicit pending-work dispositions at freeze. A row count does not establish active work. |
| Config | `/opt/infinidysk/config`, 66,222,489,600 allocated bytes | Back up the whole tree, including external state, rather than just the SQL database. |
| Legacy blobs | `/config/blobs`, 63,289,253,888 allocated bytes | Copy or take a verified immutable snapshot; include these in restore testing. |
| Other state | Native cache metadata 1,342,197,760 allocated bytes; metrics SQLite about 814 MB; prediction, Plex metadata, sweep and regrab state present | Inventory all journals, SQLite WALs, contracts and unknown settings. Preserve unsupported state in the rollback bundle. |
| Local filesystem | 253,391,142,912 bytes available at observation | Reserve capacity for config/database backup, rehearsal, target state and logs. No cache duplication is planned. |
| Delivery | rclone owns `/mnt/remote/infinidysk`; dependent containers bind it read-only with `rshared` propagation | Preserve the final path exactly. Replace the mount under a controlled consumer outage. |
| Public addresses | UI on host port 3004; backend on `192.168.20.65:8080` | Preserve both origins or update every client explicitly. v2 has one internal port, 3000. |
| Consumers | Whisparr and Stash bind the delivery mount; app also sees `/mnt/plex` and `/mnt/special` | Inventory additional clients, reverse proxies and external Plex access before go-live. |
| Operations | `infinidysk-plex-import.service` inactive at observation | Preserve its prior state. Do not install or resume a replacement runner until qualified. |
| Time and identity | Application TZ `Etc/UTC`; PUID/PGID 1000; rclone command has no `--no-modtime` | Keep import TZ and ownership. Validate native mount timestamps; do not enable epoch timestamps by assumption. |
| Stored policy | `cache.mode=native`; `preflight.mode=standard` | Native cache and Preflight need explicit acceptance decisions. Missing settings do not prove a feature is unused. |

Current application mounts include four NAS cache roots, mapped as
`/cache/qnap-local`, `/cache/debrid-cache`, `/cache/synology-volume1` and
`/cache/qnap-cache2`. Preserve their exact host paths in the private operator
inventory. Do not repurpose them as writable v2 roots.

The legacy catalogue reports these folder totals:

| Folder identity | Recorded bytes |
| --- | ---: |
| `infinidysk-qnap-cache2` | 13,488,987,451,392 |
| `infinidysk-qnap-local` | 522,375,598,080 |
| `infinidysk-volume1` | 2,368,287,760,384 |
| `infinidysk-volume3` | 8,738,243,633,152 |
| Total | **25,117,894,443,008** |

These are catalogue totals, including retained/retired records. They are not a
checksum inventory, proof of mounted-volume health or the number of transferable
bytes. Do not attempt to duplicate this cache onto the local filesystem.

## Decisions required before a production trial

| Gate | Required evidence | Accountable role |
| --- | --- | --- |
| Feature usage | Review the fork inventory and actual client usage. Resolve Preflight `standard`; also check Watchtower, Warden, search/indexers/Prowlarr, addon/Stremio and unsupported clients. Agree a replacement or explicitly accepted loss for each used feature. | Product owner and operator |
| Migration fidelity | Copied real installation imports successfully, with accepted carried/skipped/unsupported report, exact ID/path/account/category checks and all unresolved repair/regrab work accounted for. | Migration operator |
| In-place cache compatibility | Qualify the implemented direct reader on the actual roots without payload copying/removal or legacy metadata rewrites. Prove lock handover, volume identity, generation/checksum validation and rollback. | Cache engineer and storage operator |
| Client compatibility | Prove real SAB, WebDAV, native FUSE, authentication, Plex and Arr behavior using isolated clients and generic canaries. | Application operator |
| Performance | Matched v1/v2 measurements on the same host and corpus, with cold/warm status, concurrency and provider budget recorded. | Validation operator |
| Recovery | Restore the v1 backup into a disposable environment and rehearse mount/service rollback. Define how post-cutover imports are reconciled. | Application and database operators |
| Release | Exact image available to nuc-1, source provenance, required CI status and secret-safe first-start configuration reviewed. Current acceptance image is local only. | Release operator |

The [fork capability inventory](fork-v2-migration.md) describes implemented fork
features. Upstream's missing-feature list is not a complete description of this
fork; for example, this fork has PAR2 work. Implementation and local fixture
success do not satisfy the production gates above.

## 1. Build a reversible rehearsal

1. Freeze the rehearsal application revision and record image digest, config
   schema, importer revision, architecture and upstream ancestor. Keep secrets
   in private files with mode 0600, outside committed documents and command logs.
2. Inventory every writer: SAB clients, library import/conversion/cleanup,
   repair/regrab, health jobs, native-cache eviction, warming/prediction refresh,
   Plex jobs and scheduled runners. Record how each is paused and restored.
3. Schedule a consistent snapshot window. Pause intake and background mutations,
   drain readers/jobs as required, then stop application/cache writers. Obtain a
   native PostgreSQL dump plus a consistent full config/blob/journal backup.
   Use proper SQLite backups or copy only after writers stop, including WAL state.
   Do not treat a live directory copy as a consistent backup.
4. Verify archive inventory/checksums, SQLite integrity and PostgreSQL dump listing;
   restore the dump to a disposable PostgreSQL instance. Establish cross-store
   consistency and the exact snapshot time. Preserve production originals.
5. Resume the original service after the rehearsal snapshot is secure. Rehearsal
   uses private copies, a separate empty target database/volume, a separate config
   directory and isolated mount path. It must not write to production state.

Allow at least space for two config-sized copies plus the target database,
backup overhead and a reserve, or put the backups on verified external storage.
Recalculate using actual logical and allocated sizes, sparse files and restore
results. Do not count a backup stored on the same disk as disaster recovery.

## 2. Rehearse database and configuration conversion

The fork's offline PostgreSQL adapter preserves all public tables and raw rows,
alongside the SQLite import adapter and a source receipt. The upstream startup
staging path selects a narrower legacy table set. Prefer the fork snapshot path
for a preservation audit, subject to successful rehearsal on the actual schema.
See [PostgreSQL source validation](validation/rust-v2-postgres-source-20261104.md).

Use the restored, frozen source database. The CLI form is:

```text
infinidysk fork-pg-snapshot NEW_PRIVATE_DIRECTORY Etc/UTC < PRIVATE_CONNECTION_FILE
infinidysk fork-pg-verify NEW_PRIVATE_DIRECTORY EXPECTED_DIGEST
```

The connection file contains the source PostgreSQL URI. Never put it in a shell
argument or paste it into a receipt. Store the receipt digest separately from the
snapshot and verify it before import. Retain `postgres-schema.json`,
`postgres-rows.jsonl`, `postgres-source.json`, the adapter database and all source
backup files. An incomplete/pending export is a failed gate.

Current source metadata reports only public base tables and no
timestamp-with-time-zone columns. Still check full adapter constraints: table
and column bounds, 16,777,216-byte row limit and 4,294,967,296-byte
file/aggregate raw-data limits.
The database size alone does not prove exported text will fit. Do not bypass a
rejection, alter the receipt or silently switch import paths.

Copy the matching blobs, metrics and fork journals from the same frozen snapshot.
Make the selected source provider and `db-contract.json` agree with the verified
adapter. Ensure import cannot reconnect to the live source PostgreSQL instance.
Configure `INFINIDYSK_DATABASE_URL` only for the **new empty target** if PostgreSQL
is chosen for v2. The target must not be the v1 database or a populated rehearsal
database. Record the exact startup configuration privately.

Compare exact source and imported identities, history, categories, keys, provider
policy and completed-pointer behavior. Review unsupported/unknown settings and
the immutable regrab/repair holds. Keep uncertain work held; no remote replay is
authorized by an imported row. Exercise restart and repeated import protections.
Export the preserved fork state and verify its receipt. A fork export does not
include cache payloads or replace the complete rollback backup.

## 3. Isolate first start and cache qualification

Imported configuration can point at production services and library roots.
Review it **before starting**. Start with no production SAB intake, no writable
production library binds, no Arr mutations or startup webhooks, no automatic
repair/cleanup, no warming/prediction refresh, and no enabled scheduled runners.
Use egress controls or an audited proxy in addition to application toggles.
Keep real secrets and private connection details out of screenshots and reports.

Use a distinct service identity, private network and mount path. An optional
canary listener is `127.0.0.1:18080` mapped to container port 3000. Do not publish
the production ports while v1 owns them. Check the separate target database and
read-only source binds before admitting the process.

Legacy cache paths currently import inactive. Existing
[cache interoperability validation](validation/rust-v2-cache-interop-20261103.md)
qualifies a copy workflow, which is excluded by the required storage policy.
`crates/native-cache/src/legacy.rs` opens the catalogue read-only and validates
volume identity, provenance, manifests and checksums, but its `copy_block` method
publishes bytes into a different cache entry. The new explicit in-place adapter
instead validates imported provenance and repair generation on shared cache misses
and returns checksummed legacy bytes directly. It creates no payload copies.

Implementation and remaining qualification before a real-storage trial:

1. The bounded legacy read interface and shared playback/warming integration are
   implemented. Qualify them on the actual NAS with unchanged payloads and an
   exact newly built image; previous images do not contain this adapter.
2. Preserve original catalogue, manifests, sparse/chunked layout, repair ledger,
   pins and retained generations. Bind reads to imported source provenance and
   the independently derived generation. Do not silently replace legacy files.
   Define safe missing/corrupt-block behavior.
3. Transfer ownership only after all v1 writers stop and both SQLite ledgers are
   checkpointed. The adapter rejects nonempty WALs and uses immutable read-only
   SQLite connections to avoid sidecar writes. Require the original
   device/inode registration, volume marker and exclusive owner lock. Reject a
   changed/unavailable mount. Never edit identity records to force acceptance.
4. Keep v2 metadata separate and additive. If new fills are needed, specify a
   non-colliding namespace on the same storage with shared capacity accounting.
   This design must not copy or evict existing payloads. It needs review and
   qualification before enabling writes; v2 must not initialize a Rust format
   over the legacy roots. Existing-cache eviction/removal stays disabled.
5. Prove providerless full reads and seeks after restart, both delivery paths,
   repair-generation safety, interrupted handover, lock contention, volume loss,
   corruption, permissions, sparse/chunked layouts and rollback to the original
   C# reader. Verify a before/after inventory of legacy payload and metadata
   hashes, sizes and allocation. No cache data may be copied or removed.

Develop and test against generated C# fixtures first. A real-root rehearsal
requires an outage for exclusive handover, with legacy roots bound read-only
and all mutation paths disabled. The current reader opens the owner lock
read-only; prove lock support on the actual NAS/mount setup. Release it before
restarting v1. A copied catalogue may be used as audit evidence, but a payload
clone would not qualify the actual mount identity and is outside this plan.

After read compatibility passes, explicitly qualify any additive v2 fills on
the same storage while retaining the original files unchanged. Measure free
space per volume and reserve capacity; no extra capacity may be assumed from
catalogue totals. If the required behavior cannot be proven, remain on v1.

### Existing write-integrity tests and remaining qualification

The Rust-native cache already has automated tests for committed writes surviving
restart, rejection of short blocks, corruption becoming a cache miss followed by
verified refill, changed-source/length isolation, concurrent writes into one
chunk, read-only folders, quotas, owner exclusion, changed mount markers and
symlink redirection. See [store tests](../crates/native-cache/tests/store.rs).
The writer records invalid coverage first, syncs payload and recovery records,
then publishes valid catalogue coverage; reads verify stored checksums.

[Recovery tests](../crates/native-cache/src/manifest.rs) cover lost catalogue,
corrupt data, changed generations, interrupted journal tails and restart/resume.
[Repair tests](../crates/native-cache/tests/repair.rs) cover failed publication,
readback reconciliation, cancellation and unresolved writes across owner changes.
[Legacy tests](../crates/native-cache/tests/legacy.rs) reject corrupt/linked source
payloads and live owners, but currently exercise copying, not the required
in-place delivery path.

These tests do not establish immunity to corruption or certify the actual NAS.
Before enabling production writes, add or execute isolated fault qualification
for disk-full/short-write/I/O and fsync errors, process death at each publication
boundary, concurrent read/write/cancellation, NAS disconnect/remount and recovery.
Use disposable storage for destructive faults, never the production roots.
Require no invalid bytes served, no invented coverage, no modified legacy data,
and successful restart/readback or explicit fail-closed behavior. Repeat the
relevant cases against the new in-place adapter and same-storage fill design.

On 2026-11-07, `cargo test -p infinidysk-native-cache --locked` passed
**45 tests, zero failures, one ignored C# fixture case** on application source
`0c577cc1`. The earlier acceptance report records separate original-C# fixture
qualification on the same source. The focused rerun used generated local storage,
not production cache volumes. Logs: `scrap/upstream-sync-20261107/plan-native-cache-tests.log`.

## 4. Qualify live behavior and performance

Use separately authorized canary connections and an explicit provider connection
budget. Do not run both engines against unrestricted copies of the same provider
limits. Use one engine at a time or a documented split budget. Keep write-capable
Arr/Plex operations behind canary identities until their exact effects are tested.

Cover stored RAR4/RAR5, 7z, multipart/encrypted cases, direct files, missing/corrupt
articles and repairable samples. Check full bytes, random ranges, real decoding,
FUSE UID 1000 permissions/timestamps, cancellation, restart and multiple streams.
Check WebDAV `/.ids` and completed pointers, SAB IDs/categories/history, existing
library symlinks, Plex discovery/seek and client authentication. Record failure
dispositions without production media names or secrets.

Run matched v1/v2 cold and warm measurements on the same host, corpus and limits.
Record cache generation, provider requests/bytes, p50/p95 time to first byte,
seek completion, sustained bytes/s, CPU, memory and error rate. Repeat enough to
characterize noise. Current generated-loopback v2 results are not a v1 comparison.

Proposed acceptance limits, to agree before measuring: no byte mismatch, duplicate
dispatch or unauthorized write; no unexpected provider reads for fully verified
warm samples; p95 first-byte/seek latency at most 10% worse than v1; sustained
throughput at least 90% of v1 at equal concurrency and provider budget. These are
proposals, not measured results. Report absolute timings and variability as well.

## 5. Final freeze and controlled cutover

Proceed only after all gates are signed off and deployment is separately
authorized. Generate a fresh final snapshot; never promote the stale rehearsal
database and lose imports made since that snapshot.

1. Record the rollback image, effective compose files, secrets locations, database
   identity, mount propagation, old cache volumes and client settings. Verify
   backups and restore evidence. Confirm storage capacity and a bounded outage.
2. Pause all intake, record exact queue and repair/regrab dispositions, drain
   background work and active consumers, then stop all v1 writers. Produce and
   verify the final consistent PostgreSQL/config/blob/cache metadata snapshot.
3. Repeat the accepted configuration/database import into fresh v2 state with
   the qualified in-place cache adapter. Do not run verified-copy migration.
   Compare final counts, IDs, pointers, receipts and unsupported report.
   Preserve v1 data and cache untouched for rollback.
4. Stop rclone and consumers holding the old mount. Verify readers/handles are
   gone and unmount normally. Treat unmount failure as a stop condition; do not
   hide active users with a default lazy unmount.
5. Start the exact v2 image with the new database/config and qualified adapter
   to the existing cache storage, native FUSE and
   final path `/mnt/remote/infinidysk`. Verify `/mnt/remote` is shared on the host
   and use an appropriate `rshared` parent bind. Qualify `/dev/fuse`, `SYS_ADMIN`,
   AppArmor and required BDI/sysfs access in rehearsal; do not improvise them live.
6. Preserve both client origins: a reviewed compose can map host 3004 and
   `192.168.20.65:8080` to the same v2 container port 3000. Verify network aliases,
   reverse-proxy paths, base URL, auth and API keys. rclone RC port 5574 has no
   assumed native equivalent; find and migrate every dependent script first.
7. Refresh/restart only dependent media consumers after the new mount exists.
   Verify they see the replacement mount rather than a stale FUSE binding, and
   that existing symlink targets resolve at their original absolute paths.
8. Keep intake and background mutators paused while checking exact image/source,
   health/readiness, restart count, public origins, permissions and byte reads.
   Perform the qualified canary playback/seek and controlled SAB import before
   admitting normal intake. Record the first accepted v2 production write.

## 6. Soak and rollback

Propose a 24–48 hour monitored soak and at least seven days of rollback retention,
subject to measured capacity and operator agreement. Restore warming, predictions,
repair, cleanup and Plex runners one at a time under their qualified limits.
Compare accepted imports, errors, provider use, cache growth, NAS health, decoder
failures and queue terminal dispositions against the pre-cutover baseline.

Rollback triggers include identity or byte mismatch, broken library paths,
duplicate/incorrect remote dispatch, persistent mount failure, database faults
or an agreed sustained performance breach. Freeze intake and background writers
first. Preserve v2 logs, receipts, new NZBs and its database for reconciliation.

Before new v2 writes, rollback can stop v2, unmount its owned mount, restore the
original v1 service/config/PostgreSQL/cache and rclone mount, then refresh affected
consumers and validate bytes. Never point v1 at the v2 database or cache format.

**After v2 accepts new imports, rollback is not lossless.** New v2 IDs, history and
library changes are not written back to v1. Reconcile each accepted submission
against client receipts and the captured acceptance log. Resolve ownership and
link changes using the qualified journal/backup procedure. Do not blindly replay
queues or restore all links over newer state. Re-enable intake only after explicit
terminal dispositions. This reconciliation must be rehearsed before go-live.

Retire the v1 service only after the retention period, a verified v2 backup
restore, successful soak and explicit operator acceptance. The existing cache
storage and payloads remain in use; no cache removal is part of migration or
decommissioning under this plan.

## Execution record to complete

Documentation delivery checks (2026-11-07): workspace formatting and all-target
warnings-denied Clippy passed. The full locked workspace test command passed
782 tests with zero failures and six default ignores. The focused cache rerun
is recorded above. Local document links
and `git diff --check` passed. These checks did not exercise production storage.

- [ ] Feature gaps accepted; actual consumer/writer inventory complete.
- [x] Read-only in-place adapter implemented and locally qualified without cache copying/removal.
- [ ] Actual NAS ownership handover, performance, rollback and write policy qualified.
- [ ] Rehearsal snapshot, restore receipt and final migration report accepted.
- [ ] Isolated real-data, integration, NAS failure and matched performance gates passed.
- [ ] Exact deliverable image and CI status recorded; private first-start config reviewed.
- [ ] Outage, rollback retention/capacity, thresholds and post-write reconciliation agreed.
- [ ] Production execution authorized; fresh final backup/import verified.
- [ ] Cutover, canary, first-write timestamp and phased soak evidence recorded.
- [ ] Retention exit and service retirement accepted; existing cache remains in use.

## Upstream references

Reviewed 2026-11-07. Use fork code and receipts for fork-specific behavior.

- [Upgrade overview](https://www.infinidysk.com/v2/upgrade/): copied configuration and timezone preservation.
- [PostgreSQL migration](https://www.infinidysk.com/v2/upgrade/postgresql/): separate empty destination; keep the original database for rollback.
- [Replacing rclone](https://www.infinidysk.com/v2/upgrade/rclone/): native mount path, propagation and timestamp behavior.
- [First run and rollback](https://www.infinidysk.com/v2/upgrade/run/): unified port and provider/rollback cautions.
- [Upstream feature differences](https://www.infinidysk.com/v2/whats-new/): check missing upstream features against actual production use and fork implementations.
