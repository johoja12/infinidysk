# Import a legacy NzbDav library [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

!!! warning "Canary workflow"

    Keep legacy NzbDav running and keep Plex away from `/mnt/plex2`. This workflow imports only 20–50 manually selected links, creates a parallel library, and leaves the existing `/mnt/plex` tree unchanged. Do not expand the migration until validation and playback succeed.

This is a two-stage migration with an explicit trust boundary:

1. Run the export tool beside legacy NzbDav. It reads the legacy PostgreSQL database with a **SELECT-only** login, reads NZB blobs, and writes a checksummed package.
2. Mount only that package into InfiniDysk as read-only. InfiniDysk verifies every checksum before scanning or submitting anything.

The feature is advanced-only. It does not change the setup wizard or its version.

## Before you start

- Back up both applications, including InfiniDysk `/config` and the legacy database/blob store.
- Build `tools/NzbDavMigration` from the same InfiniDysk release you will run.
- Create dedicated migration categories in InfiniDysk. They must not be monitored by Sonarr or Radarr.
- Confirm InfiniDysk providers work before importing.
- Create `/mnt/plex2` on the host as a new local directory. Do not add it to Plex yet.
- Make the InfiniDysk rclone mount and its canonical `.ids` tree visible on the host, for example at `/mnt/remote/infinidysk`.
- Route throughput tests directly to backend port `8080` on the trusted Docker or local network. Port `3000` is functional for WebDAV, but its Node proxy handles every streamed byte and measurements through it are diagnostic only. Never publish `8080` to an untrusted network.

Use a separate results directory and retain the inventory, selection, export package, downloaded plan bundle, apply journal, validation output, and performance reports together.

## 1. Inventory the legacy library

Set `NZBDAV_MIGRATION_LEGACY_DB` to a PostgreSQL connection string for a SELECT-only account. Do not put the connection string in shell history, the export package, or a support report.

```bash
export NZBDAV_MIGRATION_LEGACY_DB='Host=...;Database=...;Username=...;Password=...'

dotnet run --project tools/NzbDavMigration -c Release -- \
  inventory \
  --library-root /mnt/plex \
  --blob-root /path/to/legacy/config/blobs \
  --output /path/to/results/inventory.json
```

The command walks only the supplied library root, does not follow directory symlinks, and records legacy `.ids/<guid>` links. Review every exclusion in `inventory.json`.

Choose 20–50 representative candidates across media types and representations. Create `selection.json` with exact path/ID pairs from the inventory:

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
  --selection /path/to/results/selection.json \
  --inventory /path/to/results/inventory.json \
  --blob-root /path/to/legacy/config/blobs \
  --output /path/to/results/nzbdav-canary-package \
  --package-id nuc-1-canary-2026-09-20
```

The tool writes the package through a staging directory, requires strong identity for every selected leaf, and refuses duplicates or excluded candidates. Copy the completed directory without editing it, then mount it beneath InfiniDysk's input boundary:

```yaml
services:
  infinidysk:
    volumes:
      - /host/path/config:/config
      - /host/path/nzbdav-canary-package:/config/migration-input/nzbdav-canary:ro
```

Do not mount the legacy database, credentials, live blob tree, `/mnt/plex`, or `/mnt/plex2` into InfiniDysk for this workflow.

## 3. Import through InfiniDysk

Open **Settings → System → Migration → NzbDav**.

1. Connect `/config/migration-input/nzbdav-canary`.
2. Confirm the displayed package digest, selected count, and exclusions.
3. Map every included source category to a dedicated migration-only category.
4. Keep **Submit Workers** at `1` and **Max Queue Depth** at `5` for the canary.
5. Scan and review every row. Resolve red findings before continuing.
6. To run, type the exact package digest and selected count shown by the UI.
7. Wait for import and reconciliation to reach a terminal state.
8. Review every correlation. A canary plan is unavailable while any row is ambiguous or duplicated.

An `exact` result means article identity and file size agree. Path or filename similarity is not enough. Unmatched rows remain in the report and do not receive a target.

## 4. Generate and apply `/mnt/plex2`

Generate the canary plan in the UI, then download its ZIP bundle. Extract it into a new directory; `plan.json` and `SHA256SUMS` must remain adjacent.

Apply it on the host that owns both mounts:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  apply-links \
  --plan /path/to/results/canary-plan/plan.json \
  --library-root /mnt/plex2 \
  --target-root /mnt/remote/infinidysk \
  --journal /path/to/results/apply-journal.json
```

Apply is create-only. It verifies the plan checksum, confines paths to the two supplied roots, checks the exact target and expected size, refuses symlinked parent directories, and never overwrites an existing entry. The journal is the ownership record for rollback; retain it.

Validate bounded reads before manual playback:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  validate-links \
  --journal /path/to/results/apply-journal.json \
  --output /path/to/results/validation.json \
  --ffprobe /usr/bin/ffprobe
```

Review all failures. Then manually open several files through `/mnt/plex2`, seek near the beginning, middle, and end, and confirm the original `/mnt/plex` versions still work. Do not point Plex at `/mnt/plex2` during the canary.

## 5. Record six-file speed and seek evidence

Manually choose exactly six `exact` plan rows. Include both `direct` and `rar-multipart` representations and exactly one deliberately large-file case. Create a schema-1 `benchmark-selection.json`:

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
  --selection /path/to/results/benchmark-selection.json \
  --plan /path/to/results/canary-plan/plan.json \
  --output /path/to/results/performance \
  --legacy-root /mnt/plex \
  --infinidysk-root /mnt/plex2 \
  --legacy-url http://legacy-backend:8080 \
  --legacy-route direct-backend \
  --infinidysk-url http://infinidysk-backend:8080 \
  --infinidysk-route direct-backend
```

If cache roots are available, add `--legacy-cache-root` and `--infinidysk-cache-root`; the report labels observed cache evidence. Otherwise the cache label explicitly records that it is unknown.

For each side and file the tool performs first and repeat passes, bounded reads at 10%, 50%, and 90%, plus a sequential window of up to 128 MiB. It records time to first byte, completion time, actual bytes, MiB/s, errors, timeouts, effective URL/port, route kind, and cache label in both `performance-results.json` and `performance-results.md`. Any `frontend-proxied` route is marked diagnostic and cannot support a throughput claim.

## Roll back the canary

Pause library automation before rollback. The command removes only links created by the recorded apply operation and only when each link still matches the journal:

```bash
dotnet run --project tools/NzbDavMigration -c Release -- \
  rollback-links \
  --journal /path/to/results/apply-journal.json
```

Changed, missing, or unowned entries are not deleted. Review the JSON result, confirm `/mnt/plex2` contains no unexpected remnants, and retain all evidence until the migration decision is closed. Rollback does not remove imported InfiniDysk releases; dedicated categories keep that later cleanup independently reviewable.

## Promotion gate

Do not add `/mnt/plex2` to Plex or scale beyond the canary until all of these are true:

- every selected item has a reviewed terminal import and correlation result;
- all applied links validate and representative playback succeeds;
- the six-file JSON and Markdown reports contain no unexplained errors or timeouts;
- direct-backend measurements are acceptable for both first and repeat passes;
- rollback has been rehearsed or its journal has been independently checked;
- the original NzbDav service, data, and `/mnt/plex` library remain available.

## Related

[Migration paths](../getting-started/migration.md) · [Mounting WebDAV](mounting-webdav.md) · [Backups and upgrades](backups-upgrades.md)
