# NzbDav Full-Library Import: Production Run Plan

**Status:** Plan only. No full-library inventory, import, link apply, or Plex/Arr cutover is authorized by this document.

**Objective:** Recover the live NzbDav-backed library into InfiniDysk and create a parallel `/mnt/plex2` tree with at least 90% owned, exact-target, size-validated coverage of a fresh final `/mnt/plex` inventory. Keep NzbDav and `/mnt/plex` authoritative. Plex and Arr continue using their current paths.

This is the operator sequence for the [approved recovery design](../specs/2026-09-21-nzbdav-full-library-recovery-design.md), its [implementation plan](2026-09-21-nzbdav-full-library-recovery.md), and the [migration guide](../../guides/nzbdav-migration.md). Those documents define the package formats, commands, and safety checks; this plan records the current production starting point and the gates for a full run.

## Verified starting point (2026-09-24)

| Item | Observation | Required before the full run |
| --- | --- | --- |
| InfiniDysk | nuc-1 is healthy on `local/infinidysk:413ebdb9` | Pin the exact running image and matching migration tool revision in the run record; recheck on run day. |
| Migration CLI | `/opt/infinidysk-migration/tools/current` resolves to `tools/acb6f0ce` | Verify its executable digest and that its commands match the deployed backend contract. Rebuild and install from the selected merge if either side changes. |
| Parallel canary | `/mnt/plex2` contains 30 symlinks; the retained apply journal has 30 links and the retained validation result reports 30/30 success | Revalidate the journal, current targets, sizes, and representative reads. Review source-name remediation before bulk import. |
| Full run | No full master manifest or aggregate coverage report was found under `/opt/infinidysk-migration/full` | Create a new run ID and immutable input snapshots; do not reuse the old canary run directory. |
| Legacy source | Prior checked snapshot contained 41,306 NzbDav links; orphan blob tree is currently about 47 GiB | Treat the old link count as historical. Recount from a fresh checksummed inventory and freeze a blob list. |
| Capacity | The filesystem holding `/opt/infinidysk-migration/full` has about 14 GiB free | **Block package export and bulk import.** Provide a private artifact volume and separate backup capacity first. |

The orphan scan and batch export may need a full extra copy of recoverable NZB payloads, plus one active batch, catalogue, journals, and backups. Provision a private run volume with **at least 80 GiB free initially** and a separate verified backup destination; recalculate the requirement from the frozen blob inventory before export. Keep at least 20% headroom and enough space for the largest batch and rollback evidence. Do not solve this by deleting the legacy blobs, current canary, or existing backups.

## Boundaries and evidence

- The source database uses the SELECT-only migration role and repeatable-read read-only snapshot. The source blob tree and `/mnt/plex` are read-only inputs. The migration tool never writes to NzbDav, Plex, Arr, or the legacy library.
- Store run artifacts under `/opt/infinidysk-migration/full/<new-utc-run-id>` on the provisioned volume, mode `0700`; individual artifacts remain `0600`. Record SHA-256 digests, tool/image commits, configuration, and timestamps. Do not publish raw inventories, paths, NZBs, credentials, or article IDs in issues or PRs.
- InfiniDysk sees only the current checksummed package through `/config/migration-input/...:ro`. It does not receive the legacy database, blob root, `/mnt/plex`, or `/mnt/plex2` as writable mounts.
- Every new parallel link is create-only and journaled. Its exact target must be beneath `/mnt/remote/infinidysk/.ids/`; it must never target NzbDav. Preserve any pre-existing unowned file or link as a conflict for review.
- The denominator is the fresh final inventory of valid NzbDav `.ids` symlinks under `/mnt/plex`. Every source path gets one covered or exclusion classification. At least 90% must be owned and validated in `/mnt/plex2`, including the delta pass.

## Execution sequence and hold points

### 1. Prepare capacity, backups, and a reproducible tool

1. Verify both services and rclone mounts, current app/tool revision, PostgreSQL and `/config` backups, legacy database and blob-tree backup or storage snapshot, and free space on both the run and backup volumes. Record checksums and a restore test for the backup set.
2. Freeze an operator record of the current 30-link canary, including its journal and validation result. Re-run `validate-links` and check the existing source-name remediation. Stop on a mismatched target, failed read, or unowned link; do not silently replace it.
3. Pin one self-contained `NzbDavMigration` executable under `tools/<commit>` and record its digest. Its `inventory`, `catalogue-list`, `catalogue-scan`, `recover-full`, `export-batches`, `apply-links`, `validate-links`, `coverage-report`, and `rollback-links` commands must match the selected backend. The current `acb6f0ce` binary is the starting candidate, not an assumed final pin.

**Hold:** Do not start the full scan until backups, revision compatibility, canary validation, and artifact/backup storage have passed. The current 14 GiB free on the root filesystem fails this hold point.

### 2. Inventory and recover without importing

1. Create a new private run directory. Run `inventory` against `/mnt/plex` with the SELECT-only legacy connection and `/opt/nzbdav/config/blobs`. Record the complete source-link denominator, exclusions, source inode/target snapshot, and `SHA256SUMS`.
2. Run `catalogue-list` to freeze a no-follow blob input list. Run the resumable `catalogue-scan` into a private SQLite database on the provisioned run volume. Monitor disk headroom, scanner progress, legacy service latency, and errors. Resume only against the same frozen list; seal the catalogue only when all input files are classified.
3. Run `recover-full --minimum-coverage 0.90`. Check that recovery plus every exclusion class sums exactly to the initial denominator. Inspect ambiguity, missing-article, corrupt/changed blob, and unsafe-ancestry samples without using filenames to turn an uncertain match into an exact one.

**Hold:** If uniquely recoverable article-backed links are below 90%, stop before `export-batches`, package staging, or backend submission. Publish counts and failure classes, then plan a separate remediation. Do not lower the threshold.

### 3. Export and import bounded batches

1. After the dry-run gate, seal the master manifest and export deterministic, checksummed packages. Default caps are 250 releases and 4 GiB NZB payload bytes per batch. Check projected total package bytes against available space before export; reduce caps if needed, without splitting a release.
2. Stage one completed batch at a time under the read-only migration input mount. In **Settings → System → Migration → NzbDav**, connect by digest, map only dedicated migration categories, scan, and resolve every blocking finding. Use one submit worker and queue depth five. Do not advance while another batch is active or unacknowledged.
3. Wait for terminal submission, then reconcile the same run to all-exact results. On timeout, inspect the persisted ledger before retrying; never start a duplicate run merely because a status request failed. Preserve package, run, and master digests.
4. Download and verify the complete plan bundle. Require selected = exact = actionable. Run `apply-links` with source `/mnt/plex`, parallel root `/mnt/plex2`, and target root `/mnt/remote/infinidysk`; retain its durable journal. Run `validate-links` with bounded reads and `ffprobe` where suitable. Acknowledge the batch only after every selected link validates.
5. Check service health, queue depth, disk space, NzbDav source snapshot, and both mounts after each batch. Pause and preserve state on a failed submission, non-exact mapping, source drift, target-size mismatch, apply conflict, or provider instability. Repair and resume the same batch.

### 4. Delta pass and acceptance

1. Freeze a fresh `/mnt/plex` inventory. Catalogue only new blob inputs and process added or changed links through additional deterministic batches. Report deleted source links separately; do not automatically remove their parallel links.
2. Place each unmodified initial/delta master manifest in a private coverage directory and run `coverage-report` against the live final source tree, `/mnt/plex2`, and all apply journals. Require **validated owned links / final source links ≥ 0.90**. Investigate every `missing-parallel`, `wrong-target`, drifted, or unclassified row.
3. Independently audit that every `/mnt/plex2` target is an InfiniDysk `.ids` path, every target exists at the expected size, and no legacy target was substituted. Compare `/mnt/plex` inode/target snapshots for paths that did not change between the initial and final inventories; classify legitimate source drift separately.
4. Run bounded first/repeat throughput and 10/50/90% seek checks plus representative `ffprobe`/playback samples across direct, RAR, multipart, TV, movie, 4K, small, and large files. Record route and cache state with results; verify both NzbDav and InfiniDysk remain healthy.
5. Publish a sanitized operator result: initial/final denominators, recoverable and validated counts, exact percentage, batch totals, exclusion classes, performance findings, backup locations, and rollback journal IDs. Keep `/mnt/plex2` outside Plex and Arr. Consumer registration or cutover needs its own plan and explicit request.

## Recovery and rollback

| Failure point | Response |
| --- | --- |
| Inventory, catalogue, or recovery fails | Keep NzbDav authoritative; preserve the immutable input list and partial private catalogue, fix the cause, resume or start a new run ID as appropriate. No import has occurred. |
| Dry-run coverage below 90% | Stop before export/import; report exact exclusion classes and required recovery work. |
| Batch import or correlation fails | Pause the migration queue and retain the same package/run ledger. Reconcile or repair that batch; do not create links for non-exact rows or submit the batch again blindly. |
| Link apply or validation fails | Preserve the plan and journal. Resolve the conflict without overwriting an unowned path. `rollback-links` removes only still-matching links owned by that journal; it does not remove imported releases. |
| App/database regression | Stop further batches. Restore the verified `/config` and database checkpoints only through a reviewed recovery procedure; leave NzbDav, `/mnt/plex`, Plex, and Arr unchanged. |

The exact CLI syntax and UI steps are in the [migration guide](../../guides/nzbdav-migration.md). Before executing this plan, replace the snapshot values above with a new signed run record and verify the chosen image/tool pair against the then-current `main` commit.
