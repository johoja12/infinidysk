# Import a legacy NzbDav library [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

!!! warning "Parallel-library workflow"

    Keep legacy NzbDav running and keep Plex and every Arr application away from `/mnt/plex2` and `/mnt/special2`. Start with a 20–50-link canary for each source tree. Full-library recovery uses create-only parallel trees and leaves `/mnt/plex` and `/mnt/special` unchanged; it is not a Plex cutover.

This is a two-stage migration with an explicit trust boundary:

1. Run the export tool beside legacy NzbDav. It reads the legacy PostgreSQL database with a **SELECT-only** login, reads NZB blobs, and writes a checksummed package.
2. Mount only that package into InfiniDysk as read-only. InfiniDysk verifies every checksum before scanning or submitting anything.

The feature is advanced-only. It does not change the setup wizard or its version.
It adds no `ConfigKeys` setting and is not on a new installation's critical path.

The legacy importer is **temporary migration tooling**. Keep it available for
retries, resume and reconciliation until the **entire library** is migrated and
verified, not merely until this canary passes. Imported items use the normal
InfiniDysk queue, database and streaming paths; playback must not depend on the
legacy reader remaining installed.

## Before you start

- Back up both applications, including InfiniDysk `/config` and the legacy database/blob store.
- Build `tools/NzbDavMigration` from the same InfiniDysk release you will run.
- Create dedicated migration categories in InfiniDysk. They must not be monitored by Sonarr or Radarr.
- Confirm InfiniDysk providers work before importing.
- Create `/mnt/plex2` and `/mnt/special2` on the host as separate local directories. Do not add either to Plex yet.
- Make the InfiniDysk rclone mount and its canonical `.ids` tree visible on the host, for example at `/mnt/remote/infinidysk`.
- Route throughput tests directly to backend port `8080` on the trusted Docker or local network. Port `3000` is functional for WebDAV, but its Node proxy handles every streamed byte and measurements through it are diagnostic only. Never publish `8080` to an untrusted network.

Use a separate results directory and retain the inventory, selection, export package, downloaded plan bundle, apply journal, validation output, and performance reports together.

The legacy library has two independent symlink roots. Process each source and its
parallel destination as a pair; a scan of `/mnt/plex` does **not** include
`/mnt/special`. Keep private artifacts and migration sessions separate by root:

| Legacy source | Parallel validation tree | Artifact subdirectory |
|---|---|---|
| `/mnt/plex` | `/mnt/plex2` | `plex/` |
| `/mnt/special` | `/mnt/special2` | `special/` |

The current inventory, package, apply, benchmark, and coverage commands each
accept one source root. Do not merge their inventories or plan files by relative
path: the same relative path can occur in both trees. Before export, compare
legacy DavItem IDs and original link targets across both inventories. If the same
legacy leaf appears in both, record both locations and stop the second import
until its target reuse and journal ownership have a reviewed procedure. Do not
silently import it twice or drop either link from the coverage denominator.
Full-library handling of those duplicates requires a root-qualified link
identity in the package/plan workflow, one import per unique source release,
and separate source-verified apply plans and journals for both destinations.
The current single-root commands do not provide that reuse automatically.

Keep that directory private (`0700` on Unix, with files restricted to the
operator). Inventories can contain original NZB contents and archive metadata,
including decryption material. Do not attach raw inventories or packages to a
public issue, pull request or ordinary support log. Grant the InfiniDysk service
read access only to the selected package when transferring it; never make all
migration artifacts world-readable.

## 1. Inventory the legacy library

Set `NZBDAV_MIGRATION_LEGACY_DB` to a PostgreSQL connection string for a SELECT-only account. Do not put the connection string in shell history, the export package, or a support report.

```bash
export NZBDAV_MIGRATION_LEGACY_DB='Host=...;Database=...;Username=...;Password=...'

dotnet run --project tools/NzbDavMigration -c Release -- \
  inventory \
  --library-root /mnt/plex \
  --blob-root /path/to/legacy/config/blobs \
  --output /path/to/results/plex/inventory.json

dotnet run --project tools/NzbDavMigration -c Release -- \
  inventory \
  --library-root /mnt/special \
  --blob-root /path/to/legacy/config/blobs \
  --output /path/to/results/special/inventory.json
```

Each command walks only its supplied library root, does not follow directory
symlinks, and records legacy `.ids/<guid>` links. Review exclusions and link
counts in **both** inventories. Pick canary leaves from both roots and keep
their selections and export packages separate.

### Legacy schema and recovery limits

The reader targets the legacy schema directly. It follows `DavItems.ParentId`
ancestry to a release directory referenced by `HistoryItems.DownloadDirId`.
Exactly one history owner must be found. Duplicate owners, broken or cyclic
ancestry and missing history are exclusions, not permission to match by name.

The history ID identifies the retained NZB blob. If that blob is missing, retained
`HistoryItems.NzbContents` can supply the original NZB instead. Export validates
the XML and article identity before packaging the exact validated bytes. The
source file size remains part of the later target-correlation check. Archive
member paths are relative to the proven release directory, including nested
subdirectories.

There is no requirement for `DavItems.HistoryItemId`, `FileBlobId` or `NzbBlobId`
columns. Do not add them to the legacy database to satisfy an old exporter.
Regenerate inventories made by an older exporter before using this reader.

Missing-history files and orphan NZB blobs require separate identity-backed
reconciliation through the full-recovery workflow below. It never fabricates NZBs
from incomplete metadata. Likewise, an ambiguous association or invalid source
payload is not a successful migration. Preserve those exclusions until each has a
reviewed outcome. An inventory candidate is not yet a verified import or a
playback result.

## Full-library recovery for orphan NZBs

!!! warning "Mapped-only import gate"

    The previously installed full-library CLI inventories `.ids` symlinks
    without verifying NzbDav's `LocalLinks` table. Build and install the
    mapping-aware tool from the same tested revision as the backend before
    running the commands below. Complete every hold point in the
    [mapped-only production plan](../superpowers/plans/2026-09-24-nzbdav-full-library-import-runbook.md)
    first. Only NZBs with at least one verified mapped file may be submitted;
    mixed NZBs may be imported, but only their mapped files receive parallel
    library links.

Complete the canary first. Before running these commands, back up InfiniDysk
`/config`, its database, the legacy PostgreSQL database, and the orphan blob tree.
The full-batch ledger migration is additive and auto-applies when the upgraded
container starts, but the `/config` backup is still a mandatory downgrade and
recovery checkpoint.

Use a new mode-`0700` run directory. Every inventory, SQLite catalogue, manifest,
package, plan, journal, validation result, and coverage report contains private
library or Usenet metadata. Files created by the tool are mode `0600`; do not
weaken these permissions or upload the artifacts to issues or pull requests.

```bash
umask 077
RUN=/mnt/nzbdav-cache3/infinidysk-migration-artifacts/2026-09-24T000000Z
SCRATCH=/opt/infinidysk-migration/full-scratch/2026-09-24T000000Z
mkdir -m 0700 -p "$RUN"
mkdir -m 0700 -p "$RUN/plex" "$RUN/special"
mkdir -m 0700 -p "$SCRATCH"

dotnet run --project tools/NzbDavMigration -c Release -- \
  mapped-inventory-shards \
  --library-root /mnt/plex \
  --legacy-ids-root /mnt/remote/nzbdav/.ids \
  --blob-root /opt/nzbdav/config/blobs \
  --output "$RUN/plex/initial-inventory"

dotnet run --project tools/NzbDavMigration -c Release -- \
  mapped-inventory-shards \
  --library-root /mnt/special \
  --legacy-ids-root /mnt/remote/nzbdav/.ids \
  --blob-root /opt/nzbdav/config/blobs \
  --output "$RUN/special/initial-inventory"

dotnet run --project tools/NzbDavMigration -c Release -- \
  catalogue-list \
  --blob-root /opt/nzbdav/config/blobs \
  --output "$RUN/catalogue-input.json"

dotnet run --project tools/NzbDavMigration -c Release -- \
  catalogue-scan \
  --blob-root /opt/nzbdav/config/blobs \
  --inventory "$RUN/catalogue-input.json" \
  --database "$SCRATCH/orphan-catalogue.sqlite" \
  --summary "$RUN/orphan-catalogue-summary.json"

cp "$SCRATCH/orphan-catalogue.sqlite" "$RUN/orphan-catalogue.sqlite"
test "$(sha256sum "$SCRATCH/orphan-catalogue.sqlite" | cut -d' ' -f1)" = \
  "$(sha256sum "$RUN/orphan-catalogue.sqlite" | cut -d' ' -f1)"

dotnet run --project tools/NzbDavMigration -c Release -- \
  recover-shards \
  --inventory "$RUN/plex/initial-inventory" \
  --catalogue "$SCRATCH/orphan-catalogue.sqlite" \
  --catalogue-summary "$RUN/orphan-catalogue-summary.json" \
  --output "$RUN/plex/recovery" \
  --minimum-coverage 0.90

dotnet run --project tools/NzbDavMigration -c Release -- \
  recover-shards \
  --inventory "$RUN/special/initial-inventory" \
  --catalogue "$SCRATCH/orphan-catalogue.sqlite" \
  --catalogue-summary "$RUN/orphan-catalogue-summary.json" \
  --output "$RUN/special/recovery" \
  --minimum-coverage 0.90

dotnet run --project tools/NzbDavMigration -c Release -- \
  verify-sharded-roots \
  --plex-inventory "$RUN/plex/initial-inventory" \
  --special-inventory "$RUN/special/initial-inventory" \
  --plex-recovery "$RUN/plex/recovery" \
  --special-recovery "$RUN/special/recovery"
```

`catalogue-list` freezes a no-follow input snapshot. `catalogue-scan` commits in
bounded transactions and can be run again with the same inventory/database after
an interruption; already completed snapshots are skipped. Do not edit or replace
the frozen blob files while scanning. Keep the SQLite catalogue on local
`$SCRATCH`, verify free space there, and copy a checksummed sealed copy to `$RUN`
for recovery. Recovery adds a blob-path lookup index to older catalogues when
needed, so run it against the writable local copy and retain the sealed NAS copy
as the unchanged checkpoint. Verify that the artifact share is the mounted volume 3 NFS export
before writing; keep backups on the separate volume 2 share. Review each root's
inventory and recovery `manifest.json`, shard masters, and exclusions. Shards
resume only when their row and catalogue digests still match. Export is blocked unless
at least 90% of each root's `LocalLinks` rows have an exact article-backed payload.
Byte-identical NZB copies collapse to one logical payload; distinct matches
remain ambiguous. Cross-root verification blocks export when the roots share
DavItem IDs or NZB payloads; those require the separate target-reuse workflow.
When the sealed catalogue was scanned from a verified backup tree, pass that
same tree as `--payload-root` for export. Keep `--blob-root` pointed at the live
legacy blob tree used by the mapped inventory for its drift check.

Export immutable batches. The defaults are at most 250 releases and 4 GiB of NZB
payload bytes per batch; lower either bound to reduce queue or review pressure.
One oversized release is isolated and explicitly marked rather than hidden.

### Start the isolated scenes root before Plex recovery finishes

When `/mnt/special` has a complete mapped recovery above 90% but the much larger
`/mnt/plex` recovery is still running, `export-special-ahead-batches` can prepare
the `scenes/` leaves from the sealed special recovery. It verifies the complete
Plex mapped inventory, rejects shared DavItem IDs, and checks every selected
special link against current `LocalLinks` and its original symlink target. New
source links do not invalidate previously verified links; account for them in
the final live coverage pass. Recovered releases whose legacy job names would
change on ID submission are counted and excluded for separate remediation; the
remaining exact `scenes/` links must still cover at least 90% of the sealed
special mapped inventory.
It does not claim that Plex has passed recovery or
that all cross-root NZB payloads are known. Import the first bounded special
batch, review its exact plan and validation, and keep Plex import on hold until
the regular two-root verification succeeds. Do not register `/mnt/special2`
with Plex or Arr during this staged run.

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  export-special-ahead-batches \
  --plex-inventory "$RUN/plex/initial-inventory" \
  --special-inventory "$RUN/special/initial-inventory" \
  --special-recovery "$RUN/special/recovery" \
  --payload-root /path/to/the/sealed/special-payload-tree \
  --output "$RUN/special/ahead-batches" \
  --first-batch-releases 30
```

The output uses the same full-batch package and acknowledgement flow below.
Store its master digest and batch sequence; do not generate a second sequence
for the same special root while the first remains active.
The two roots have separate master digests and batch sequences. Finish and
acknowledge every `special` batch before connecting the first `plex` batch. For a
20–50 file `/mnt/special2` canary, use `--first-batch-releases` and inspect the
first package's selected-link count before connecting it. Adjust and re-export
the immutable package set if that count is outside the canary range. Later
special batches retain the normal 250-release cap.

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  export-sharded-batches \
  --root special \
  --plex-inventory "$RUN/plex/initial-inventory" \
  --special-inventory "$RUN/special/initial-inventory" \
  --plex-recovery "$RUN/plex/recovery" \
  --special-recovery "$RUN/special/recovery" \
  --blob-root /opt/nzbdav/config/blobs \
  --payload-root /opt/nzbdav/config/blobs \
  --output "$RUN/special/batches" \
  --first-batch-releases 30 \
  --max-releases 250 \
  --max-payload-bytes 4294967296

dotnet run --project tools/NzbDavMigration -c Release -- \
  export-sharded-batches \
  --root plex \
  --plex-inventory "$RUN/plex/initial-inventory" \
  --special-inventory "$RUN/special/initial-inventory" \
  --plex-recovery "$RUN/plex/recovery" \
  --special-recovery "$RUN/special/recovery" \
  --blob-root /opt/nzbdav/config/blobs \
  --payload-root /opt/nzbdav/config/blobs \
  --output "$RUN/plex/batches" \
  --max-releases 250 \
  --max-payload-bytes 4294967296
```

Bind only one completed batch at a time beneath
`/config/migration-input/...:ro`. Register each full batch with
`POST /api/migration/nzbdav/full/connect` using its package path, master digest,
root-specific mapped and recoverable counts, one submit worker, and queue depth
five. The current settings page does not expose this full-batch registration
request. Then use **Settings → System → Migration → NzbDav** to scan, submit,
wait for a terminal run, and reconcile. Pause through the migration UI if
providers or the queue become unstable; resume the same batch instead of creating
a second submission. A reconnect of the same package digest is idempotent. The
next batch is fenced until the current batch has a terminal, fully exact,
fully-applied plan acknowledgement.

Use each root's own mapped and recoverable counts in its full-connect request;
the combined count is an audit gate, not a batch-master denominator. For each
root's batch, download its checksummed plan and apply it with that
root's three paths. Keep batch numbers, sessions, plans, journals, and validations
under the matching `plex/` or `special/` artifact directory; `batch-0001` in one
root is unrelated to `batch-0001` in the other.
The source root is required so the command can verify the original legacy symlink
immediately before creating its parallel link:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  apply-sharded-links \
  --plan "$RUN/plex/plans/batch-0001/plan.json" \
  --mapped-inventory "$RUN/plex/initial-inventory" \
  --source-root /mnt/plex \
  --library-root /mnt/plex2 \
  --target-root /mnt/remote/infinidysk \
  --journal "$RUN/plex/journals/batch-0001/apply-journal.json"

dotnet run --project tools/NzbDavMigration -c Release -- \
  validate-links \
  --journal "$RUN/plex/journals/batch-0001/apply-journal.json" \
  --output "$RUN/plex/journals/batch-0001/validation.json" \
  --ffprobe /usr/bin/ffprobe
```

Repeat with `special/` artifacts, its mapped inventory,
`--source-root /mnt/special`, and `--library-root /mnt/special2`.
Apply checks `LocalLinks` again before each link creation and never copies links from one source tree
to the other staging tree. Keep both staging trees out of Plex and Arr.

The plan must contain exact rows only. Apply is create-only and source-drift
fenced; retain each plan and journal as its ownership proof. Validation checks
size and bounded beginning/middle/end reads. Do not acknowledge a batch whose
validation has failures.

After the first pass, generate coverage against each root's initial snapshot
and a fresh live mapped source snapshot. The report classifies added, removed,
broken, and changed mappings without dropping new rows from the denominator.
If new links need a delta import, pause and prepare a separately reviewed
selection; rerunning the full sharded exporter would resubmit old releases.
Removed links need no parallel entry, and changed legacy targets must be
reviewed rather than forced.

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  sharded-coverage-report \
  --inventory "$RUN/plex/initial-inventory" \
  --recovery "$RUN/plex/recovery" \
  --library-root /mnt/plex2 \
  --blob-root /opt/nzbdav/config/blobs \
  --journals-dir "$RUN/plex/journals" \
  --output "$RUN/plex/coverage" \
  --minimum-coverage 0.90

dotnet run --project tools/NzbDavMigration -c Release -- \
  sharded-coverage-report \
  --inventory "$RUN/special/initial-inventory" \
  --recovery "$RUN/special/recovery" \
  --library-root /mnt/special2 \
  --blob-root /opt/nzbdav/config/blobs \
  --journals-dir "$RUN/special/journals" \
  --output "$RUN/special/coverage" \
  --minimum-coverage 0.90
```

The final live `LocalLinks` snapshot beneath **each** source
root is its denominator, including broken and missing source links. Review every `covered`,
`missing-parallel`, `wrong-target`, `added-after-initial`, and removed item in
both `coverage.json` and `coverage.md` reports; the counts must classify every
final source link exactly once within its root. Calculate combined coverage as
`(plex covered + special covered) / (plex final source + special final source)`
from the two reports. Require both per-root reports to pass their 90% gate and
review the combined result; neither a single-root report nor the combined ratio
is permission to register the parallel trees with Plex or Arr.

To undo a reviewed batch, pause migration activity and use only its journal:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  rollback-links \
  --journal "$RUN/plex/journals/batch-0001/apply-journal.json"
```

Rollback removes only unchanged links owned by that journal. It does not delete
imported InfiniDysk releases, source links, packages, or evidence.
Use the matching `special/` journal to roll back a `/mnt/special2` batch.

### Replacing a failed canary import

Keep replacement work inside the dedicated canary boundary. Only history entries
whose reconciled migration ledger IDs resolve to the dedicated `migration-*`
categories, and links owned by the matching `/mnt/plex2` or `/mnt/special2`
apply journal, are eligible.
Freshly reconcile the IDs and counts immediately before deleting anything. Stop on
any ownership or count mismatch.

Do not delete or modify the legacy NzbDav deployment, `/mnt/plex`, `/mnt/special`, Plex libraries,
Sonarr or Radarr configuration, unrelated InfiniDysk history, or shared migration
tooling. Delete imported content through the supported SAB history API with completed
files enabled, remove only verified canary links, reset and forget the matching
migration session through its supported APIs, then generate a new checksummed package
instead of reusing or editing the defective package.

For the success canary, exclude corrupted, zero-padded, quarantined and repaired
or repairing files. A previously repaired file may rely on local patches that
the original NZB does not contain. Repeated normalized article IDs within an NZB
are also excluded from this workflow rather than silently choosing one entry.

Choose 20–50 representative candidates **from each source root** across media
types and representations. The `export` command enforces 20–50 links per
selection. Create a separate
`selection.json` for each source root with exact path/ID pairs from its inventory:

```json
{
  "Items": [
    {
      "LibraryRelativePath": "Movies/Example (2024)/Example.mkv",
      "LegacyDavItemId": "00000000-0000-0000-0000-000000000001"
    }
  ]
}
```

## 2. Create and transfer the export package

The destination must not already exist:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  export \
  --selection /path/to/results/plex/selection.json \
  --inventory /path/to/results/plex/inventory.json \
  --blob-root /path/to/legacy/config/blobs \
  --output /path/to/results/plex/nzbdav-canary-package \
  --package-id nuc-1-plex-canary-2026-09-20
```

Repeat for `special/` with its own selection, inventory, output package, and
unique package ID. Review the cross-root ID comparison before exporting either
package.

The tool writes the package through a staging directory, requires strong identity
for every selected leaf, and refuses duplicates or excluded candidates. Each release
records an authoritative `sourceFileName` and `sourceJobName`; its `payloadPath` is
only the private checksummed location inside the package and may intentionally use an
opaque release ID. InfiniDysk submits the authoritative filename through its normal
queue path, which preserves the legacy job name and gives media deobfuscation a
human-readable fallback.

Older packages without the two source-name fields remain readable for compatibility,
but they fall back to the payload basename. Regenerate them with the upgraded export
tool before any name-preserving import or replacement run. Do not edit a manifest in
place because doing so invalidates the package checksums.

Copy the completed directory without editing it, then mount it beneath InfiniDysk's
input boundary:

```yaml
services:
  infinidysk:
    volumes:
      - /host/path/config:/config
      - /host/path/plex/nzbdav-canary-package:/config/migration-input/nzbdav-plex-canary:ro
      - /host/path/special/nzbdav-canary-package:/config/migration-input/nzbdav-special-canary:ro
```

Do not mount the legacy database, credentials, live blob tree, `/mnt/plex`,
`/mnt/special`, `/mnt/plex2`, or `/mnt/special2` into InfiniDysk for this workflow.

## 3. Import through InfiniDysk

Open **Settings → System → Migration → NzbDav**.

1. Connect `/config/migration-input/nzbdav-plex-canary`. Complete and download
   its plan before connecting `/config/migration-input/nzbdav-special-canary`
   for a separate run. Apply and validate each root with its matching plan.
2. Confirm the displayed package digest, selected count, and exclusions.
3. Map every included source category to a dedicated migration-only category.
4. Keep **Submit Workers** at `1` and **Max Queue Depth** at `5` for the canary.
5. Scan and review every row. Resolve red findings before continuing.
6. To run, type the exact package digest and selected count shown by the UI.
7. Wait for import and reconciliation to reach a terminal state.
8. Review every correlation for each run. A canary plan is unavailable while any
   row is ambiguous or duplicated.

An `exact` result means article identity and file size agree. Path or filename similarity is not enough. Unmatched rows remain in the report and do not receive a target.

## 4. Generate and apply both parallel trees

Generate the canary plan in the UI, then download its ZIP bundle. Extract it into a new directory; `plan.json` and `SHA256SUMS` must remain adjacent.

Apply it on the host that owns both mounts:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  apply-links \
  --plan /path/to/results/plex/canary-plan/plan.json \
  --source-root /mnt/plex \
  --library-root /mnt/plex2 \
  --target-root /mnt/remote/infinidysk \
  --journal /path/to/results/plex/apply-journal.json
```

Repeat for the `special/` plan with `--source-root /mnt/special`,
`--library-root /mnt/special2`, and a `special/` journal. Never apply a plan
against the other source root, even if relative link paths look the same.

Apply is create-only. It verifies the plan checksum, confines paths to the two supplied roots, checks the exact target and expected size, refuses symlinked parent directories, and never overwrites an existing entry. The journal is the ownership record for rollback; retain it.

Validate bounded reads before manual playback:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  validate-links \
  --journal /path/to/results/plex/apply-journal.json \
  --output /path/to/results/plex/validation.json \
  --ffprobe /usr/bin/ffprobe
```

Validate the `special/` journal separately. Review all failures. Manually open
several files through each parallel tree, seek near the beginning, middle, and
end, and confirm that their originals under `/mnt/plex` or `/mnt/special` still
work. Do not point Plex at either parallel tree during the canary.

## 5. Record speed and seek evidence for both roots

The benchmark tool requires exactly six rows per selection and accepts one
source root. Manually choose six `exact` plan rows from **each** root, for 12
files overall. In each six-file set, include both `direct` and `rar-multipart`
representations where available and exactly one deliberately large-file case.
Create a separate schema-1 `benchmark-selection.json` for each root:

```json
{
  "schemaVersion": 1,
  "selectionMode": "reviewed-manual",
  "files": [
    {
      "libraryRelativePath": "Movies/Example (2024)/Example.mkv",
      "legacyDavItemId": "00000000-0000-0000-0000-000000000001",
      "infiniDyskDavItemId": "00000000-0000-0000-0000-000000000002",
      "expectedFileSize": 123456789,
      "representation": "direct",
      "resolutionClass": "1080p",
      "isLargeFileCase": false
    }
  ]
}
```

Run the paired benchmark without purging caches or restarting either service between passes:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  benchmark-links \
  --selection /path/to/results/plex/benchmark-selection.json \
  --plan /path/to/results/plex/canary-plan/plan.json \
  --output /path/to/results/plex/performance \
  --legacy-root /mnt/plex \
  --infinidysk-root /mnt/plex2 \
  --legacy-url http://legacy-backend:8080 \
  --legacy-route direct-backend \
  --infinidysk-url http://infinidysk-backend:8080 \
  --infinidysk-route direct-backend
```

Run a separate `benchmark-links` command for the `special/` selection and plan,
using `/mnt/special` and `/mnt/special2` as the two roots. Review both reports
as two six-file sets; neither command measures paths under the other root.

If cache roots are available, add `--legacy-cache-root` and
`--infinidysk-cache-root`. When a root uses rclone's standard `vfs/<remote>`
layout, the report reads the sibling `vfsMeta/<remote>` range journal.
`observed-warm` requires complete cached byte coverage; sparse logical file length
is not proof. Partial entries report cached bytes and percentage. Missing or
inconsistent metadata remains unknown. Without cache roots, the cache label
explicitly records that its state is unknown.

For each side and file the tool performs first and repeat passes, bounded reads at 10%, 50%, and 90%, plus a sequential window of up to 128 MiB. It records time to first byte, completion time, actual bytes, MiB/s, errors, timeouts, effective URL/port, route kind, and cache label in both `performance-results.json` and `performance-results.md`. Any `frontend-proxied` route is marked diagnostic and cannot support a throughput claim.

## Roll back the canary

Pause library automation before rollback. The command removes only links created by the recorded apply operation and only when each link still matches the journal:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  rollback-links \
  --journal /path/to/results/plex/apply-journal.json
```

Use the `special/` journal separately for `/mnt/special2`. Changed, missing, or
unowned entries are not deleted. Review both results, confirm both parallel trees
contain no unexpected remnants, and retain all evidence until the migration
decision is closed. Rollback does not remove imported InfiniDysk releases;
dedicated categories keep that later cleanup independently reviewable.

## Promotion gate

Do not add `/mnt/plex2` or `/mnt/special2` to Plex or scale beyond the canary
until all of these are true:

- every selected item has a reviewed terminal import and correlation result;
- all applied links validate and representative playback succeeds;
- both six-file JSON and Markdown reports contain no unexplained errors or timeouts;
- direct-backend measurements are acceptable for both first and repeat passes;
- rollback has been rehearsed or its journal has been independently checked;
- the original NzbDav service, data, `/mnt/plex`, and `/mnt/special` libraries remain available.

## Retire the temporary importer

After the canary is accepted, use a separately reviewed full-library migration
and cutover plan that preserves the existing Plex library paths, metadata and
watch state. Passing the canary does not authorize bulk import or replacing the
production symlink tree.

Keep the temporary exporter/import adapter until the full library has been
reconciled, unresolved records have reviewed dispositions, and playback works
independently of legacy NzbDav. Revoke temporary legacy database credentials
between export batches when not needed; retaining the tool does not require
leaving its access enabled. Remove the adapter after full acceptance.
Retain the checksummed packages, mapping reports and journals as private audit
and recovery evidence. Do not remove shared migration infrastructure needed by
other import sources, or the permanent native-cache and Plex-prefetch features.

## Related

[Migration paths](../getting-started/migration.md) · [Mounting WebDAV](mounting-webdav.md) · [Backups and upgrades](backups-upgrades.md)
