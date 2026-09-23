# Mapped NzbDav Library Import: Production Run Plan

**Status:** Revised plan only. The attempted full-library preflight was stopped on 2026-09-24 before inventory, recovery, export, or submission. Its newly created backup and run artifacts were removed; the existing canary and older NAS backups were preserved. The currently installed CLI does not enforce mapped-only selection, so no bulk import may resume with it.

**Objective:** Import only NzbDav NZBs proven to belong to the currently mapped library into InfiniDysk. Create parallel `/mnt/plex2` links only for eligible mapped files, with at least 90% owned, exact-target, size-validated coverage of a fresh final mapped inventory. Keep NzbDav and `/mnt/plex` authoritative. Plex and Arr continue using their current paths.

This is the operator sequence for the [approved recovery design](../specs/2026-09-21-nzbdav-full-library-recovery-design.md), its [implementation plan](2026-09-21-nzbdav-full-library-recovery.md), and the [migration guide](../../guides/nzbdav-migration.md). Those documents define the package formats, commands, and safety checks; this plan records the current production starting point and the gates for a full run.

## Verified starting point (2026-09-24)

| Item | Observation | Required before the full run |
| --- | --- | --- |
| InfiniDysk | nuc-1 is healthy on `local/infinidysk:413ebdb9` | Pin the exact running image and matching migration tool revision in the run record; recheck on run day. |
| Migration CLI | `/opt/infinidysk-migration/tools/current` resolves to `tools/acb6f0ce` | Verify its executable digest and that its commands match the deployed backend contract. Rebuild and install from the selected merge if either side changes. |
| Parallel canary | `/mnt/plex2` contains 30 symlinks; the **current** naming-reimport journal at `/opt/infinidysk-migration/canary/naming-reimport-20260922T185759Z/apply-journal-run2.json` revalidated 30/30 | Use this journal for future checks. The superseded September 21 journal fails all 30 because its targets no longer match; preserve it as historical evidence only. |
| Mapped source | A read-only live audit found 41,411 NzbDav `LocalLinks` rows and 41,411 matching `/mnt/plex` symlinks; every target basename matched its `DavItemId`, no row was marked broken, and no target escaped the NzbDav `.ids` root | Treat this as a snapshot, not a permanent allowlist. Recheck row-by-row in the same read-only source snapshot used for each inventory. |
| Full run | The attempted `20260923T214300Z-full-library` preflight created no inventory, catalogue, package, submission, or new link; all of its new artifacts were removed | Start a fresh run ID only after the mapped-only CLI gates below are implemented and verified. |
| Legacy blobs | The orphan blob tree is about 47 GiB | Catalogue blobs only as evidence for eligible mapped leaves; an orphan NZB is never importable merely because it appears in the catalogue. |
| Capacity | The root filesystem holding `/opt/infinidysk-migration/full` was about 89% used at the latest check | **Block package export and bulk import.** Provide private artifact storage with calculated headroom; root is only suitable for bounded local SQLite scratch. Recheck free space on run day. |
| Backup destination | `/mnt/docker/infinidysk-migration-backups` is a mounted Synology NFS share with ample space; older backups and verified local backup copies remain there | Create a **new** full-run backup on this share. The interrupted preflight backup was removed and is not a recovery point. Verify the NFS mount before writing, read back checksums, and restore-test. Do not write to the local mountpoint if the NAS is unavailable. |

The orphan scan and batch export may need a full extra copy of recoverable NZB payloads, plus one active batch, catalogue, and journals. Provision a private run volume with **at least 80 GiB free initially** and use the separate NAS backup destination above; recalculate the artifact requirement from the frozen blob inventory before export. Keep at least 20% headroom and enough space for the largest batch and rollback evidence. Do not solve this by deleting the legacy blobs, current canary, or existing backups.

## Mapped-only selection contract

NzbDav records mapping in `LocalLinks`, one row per mapped file (`LinkPath`, `DavItemId`, `IsBroken`). A file is eligible only when its row is not broken, its path is strictly beneath `/mnt/plex`, and that exact path is a symlink into the NzbDav `.ids` tree whose **final ID component** equals the row's `DavItemId`. The `.ids` target has shard directories before the ID; do not require the ID immediately after `.ids/`. Missing, changed, duplicate, or mismatched rows/links are classified and reviewed, never inferred from names or counts.

The mapped file set is the authoritative source scope. A recovered NZB is eligible for package export only when strong article evidence connects it to at least one eligible mapped file; do not export unrelated retained history, orphan blob payloads, or NZBs that match only unmapped files. The catalogue may scan the blob tree to find evidence, but scanning does not authorize submission. The package's selected links must be a subset of the frozen mapped allowlist, and `export-batches` must fail closed if any release has no mapped selection. Recheck mapping membership and the original symlink immediately before each link apply.

**Mixed NZBs:** Import a whole NZB when it has both mapped and unmapped files, but select and create `/mnt/plex2` links only for its mapped files. The normal InfiniDysk import may create unlinked entries for other files in that NZB; record their count in the run report. Never treat those extra entries as mapped coverage or create parallel links for them. This is the operator-selected policy for this run.

**Implementation gate:** The current `NzbDavMigration inventory` reads filesystem `.ids` links and legacy `DavItems` but does not join `LocalLinks`; `recover-full`, `export-batches`, and `coverage-report` consequently cannot enforce this contract. Extend the CLI and its focused tests before another full run. Read `LocalLinks` and `DavItems` in one repeatable-read, read-only transaction; seal the mapped allowlist and its digest in the inventory/master; reject missing or changed provenance at export, apply, and final coverage. Test stale links, unmapped blobs, broken rows, sharded targets, mixed releases with unlinked imported entries, and mapping drift.

For coverage, report the total mapped DB rows, valid mapped source links, mapping conflicts, and out-of-scope filesystem links separately. The 90% denominator is **all fresh final `LocalLinks` rows under `/mnt/plex`**, including broken or conflicting rows as exclusions; do not make the percentage look better by dropping them from the denominator. It is not all blobs, all history NZBs, or all `.ids` links found on disk. A source mapping conflict must be resolved or explicitly classified before the run advances.

## Boundaries and evidence

- The source database uses the SELECT-only migration role and repeatable-read read-only snapshot. The source blob tree and `/mnt/plex` are read-only inputs. The migration tool never writes to NzbDav, Plex, Arr, or the legacy library.
- Store run artifacts under `/opt/infinidysk-migration/full/<new-utc-run-id>` on the provisioned volume, mode `0700`; individual artifacts remain `0600`. Record SHA-256 digests, tool/image commits, configuration, and timestamps. Do not publish raw inventories, paths, NZBs, credentials, or article IDs in issues or PRs.
- InfiniDysk sees only the current checksummed package through `/config/migration-input/...:ro`. It does not receive the legacy database, blob root, `/mnt/plex`, or `/mnt/plex2` as writable mounts.
- Every new parallel link is create-only and journaled. Its exact target must be beneath `/mnt/remote/infinidysk/.ids/`; it must never target NzbDav. Preserve any pre-existing unowned file or link as a conflict for review.
- Every mapped source row gets one covered or exclusion classification. At least 90% of the fresh final mapped DB set must be owned and validated in `/mnt/plex2`, including the delta pass.

## Execution sequence and hold points

### 1. Prepare capacity, backups, and a reproducible tool

1. Verify both services and rclone mounts, current app/tool revision, PostgreSQL and `/config` backups, legacy database and blob-tree backup or storage snapshot, and free space on both the run and backup volumes. Record checksums and a restore test for the backup set.
2. Freeze an operator record of the current 30-link canary, including the **current** naming-reimport journal and validation result. Re-run `validate-links` against that journal and check the existing source-name remediation. Stop on a mismatched target, failed read, or unowned link; do not silently replace it.
3. Implement and test the mapped-only selection contract above, then pin one self-contained `NzbDavMigration` executable under `tools/<commit>` and record its digest. Its `inventory`, `catalogue-list`, `catalogue-scan`, `recover-full`, `export-batches`, `apply-links`, `validate-links`, `coverage-report`, and `rollback-links` commands must match the selected backend. The current `acb6f0ce` binary is **not** eligible for bulk use because it lacks the `LocalLinks` gate.

**Hold:** Do not start the full scan until the mapped-only CLI and tests, backups, revision compatibility, canary validation, and artifact/backup storage have passed. The root filesystem remains nearly 90% used and is not the dedicated artifact volume required by this plan.

### 2. Inventory and recover without importing

1. Create a new private run directory. Use the revised `inventory` against `/mnt/plex` with the SELECT-only legacy connection and `/opt/nzbdav/config/blobs`. Freeze `LocalLinks` and `DavItems` in the same read-only source snapshot, match every selected row to its current symlink and ID, and record the mapped allowlist digest, full mapped denominator, mapping conflicts, source inode/target snapshot, and `SHA256SUMS`. Stop on an unexplained mismatch.
2. Run `catalogue-list` to freeze a no-follow blob input list. Run the resumable `catalogue-scan` with its SQLite database on local storage and checksummed immutable outputs on the provisioned artifact volume. Monitor disk headroom, scanner progress, legacy service latency, and errors. Resume only against the same frozen list; seal the catalogue only when all input files are classified. Catalogue contents are evidence, not the import selection.
3. Run the revised `recover-full --minimum-coverage 0.90` over eligible mapped leaves only, while counting ineligible mapped rows as exclusions in the denominator. Check that recovered and excluded mapped rows sum exactly to the full mapped denominator. Inspect ambiguity, missing-article, corrupt/changed blob, and unsafe-ancestry samples without using filenames to turn an uncertain match into an exact one. Verify that every proposed NZB has mapped-link evidence and that no orphan-only release enters the master manifest.

**Hold:** If uniquely recoverable article-backed links are below 90%, stop before `export-batches`, package staging, or backend submission. Publish counts and failure classes, then plan a separate remediation. Do not lower the threshold.

### 3. Export and import bounded batches

1. After the dry-run gate, seal the mapped allowlist and master manifest, then export deterministic, checksummed packages containing **only NZBs with eligible mapped files**. Default caps are 250 releases and 4 GiB NZB payload bytes per batch. Check projected total package bytes against available space before export; reduce caps if needed, without splitting a release. Compare every batch's selected mapped IDs and payload provenance with the sealed allowlist before staging; count any other files carried by a mixed NZB separately.
2. Stage one completed batch at a time under the read-only migration input mount. In **Settings → System → Migration → NzbDav**, connect by digest, map only dedicated migration categories, scan, and resolve every blocking finding. Use one submit worker and queue depth five. Do not advance while another batch is active or unacknowledged.
3. Wait for terminal submission, then reconcile the same run to all-exact results. On timeout, inspect the persisted ledger before retrying; never start a duplicate run merely because a status request failed. Preserve package, run, and master digests.
4. Download and verify the complete plan bundle. Require selected = exact = actionable and recheck that every selected leaf remains mapped in NzbDav. Run `apply-links` with source `/mnt/plex`, parallel root `/mnt/plex2`, and target root `/mnt/remote/infinidysk`; retain its durable journal. Run `validate-links` with bounded reads and `ffprobe` where suitable. Acknowledge the batch only after every selected mapped link validates.
5. Check service health, queue depth, disk space, NzbDav source snapshot, and both mounts after each batch. Pause and preserve state on a failed submission, non-exact mapping, source drift, target-size mismatch, apply conflict, or provider instability. Repair and resume the same batch.

### 4. Delta pass and acceptance

1. Freeze a fresh mapped `LocalLinks` plus `/mnt/plex` inventory. Catalogue only new blob inputs and process newly mapped or changed eligible links through additional deterministic batches. Report removed mappings and deleted source links separately; do not automatically remove their parallel links.
2. Place each unmodified initial/delta master manifest in a private coverage directory and run the revised `coverage-report` against the final mapped source snapshot, `/mnt/plex2`, and all apply journals. Require **validated owned mapped links / all final mapped DB rows ≥ 0.90**. Investigate every `missing-parallel`, `wrong-target`, drifted, broken, or unclassified row; out-of-scope filesystem links never enter the denominator.
3. Independently audit that every `/mnt/plex2` target is an InfiniDysk `.ids` path, every target exists at the expected size, and no legacy target was substituted. Compare `/mnt/plex` inode/target snapshots for paths that did not change between the initial and final inventories; classify legitimate source drift separately.
4. Run bounded first/repeat throughput and 10/50/90% seek checks plus representative `ffprobe`/playback samples across direct, RAR, multipart, TV, movie, 4K, small, and large files. Record route and cache state with results; verify both NzbDav and InfiniDysk remain healthy.
5. Publish a sanitized operator result: initial/final mapped DB denominators, eligible and excluded counts, mapping conflicts, recoverable and validated counts, exact percentage, mixed-NZB unlinked entry counts, batch totals, performance findings, backup locations, and rollback journal IDs. Keep `/mnt/plex2` outside Plex and Arr. Consumer registration or cutover needs its own plan and explicit request.

## Recovery and rollback

| Failure point | Response |
| --- | --- |
| Inventory, catalogue, or recovery fails | Keep NzbDav authoritative; preserve the immutable input list and partial private catalogue, fix the cause, resume or start a new run ID as appropriate. No import has occurred. |
| Dry-run coverage below 90% | Stop before export/import; report exact exclusion classes and required recovery work. |
| Batch import or correlation fails | Pause the migration queue and retain the same package/run ledger. Reconcile or repair that batch; do not create links for non-exact rows or submit the batch again blindly. |
| Link apply or validation fails | Preserve the plan and journal. Resolve the conflict without overwriting an unowned path. `rollback-links` removes only still-matching links owned by that journal; it does not remove imported releases. |
| App/database regression | Stop further batches. Restore the verified `/config` and database checkpoints only through a reviewed recovery procedure; leave NzbDav, `/mnt/plex`, Plex, and Arr unchanged. |

The exact CLI syntax and UI steps are in the [migration guide](../../guides/nzbdav-migration.md). Before executing this plan, replace the snapshot values above with a new signed run record and verify the chosen image/tool pair against the then-current `main` commit.
