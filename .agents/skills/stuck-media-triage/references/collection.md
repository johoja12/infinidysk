# Collection and correlation

## Deployment discovery

These are discovery hints observed on 2026-10-13, not current-state guarantees:

- ID: `ssh nuc-1`, Compose `/opt/docker/infinidysk`, config `/opt/infinidysk/config`, containers `infinidysk`, `infinidysk-postgres`, `rclone-infinidysk`; backend was `192.168.20.65:8080`.
- Preview: <https://plexpreview.sakhter.org/>, `ssh beelink`, container `plex-previews`, app `/app/plex_generate_previews`, state `/config/plex_previews.db`, host config `/opt/docker/plex-previews/config`.
- Consumer library paths used `/mnt/plex`; ID mount used `/mnt/remote/infinidysk`. Other deployments may use different paths.

Recheck container mounts, running revision, configured enabled Arr instances, database backend, runner/service state, and API authentication. Inspect metadata and credential **names/locations**, not secret values. Do not print full container environments, config dumps, secret files, download URLs, or exception objects containing URLs.

## Read-only collector

`../scripts/collect.py` uses only GET requests and SQLite reads. It follows no HTTP redirects, masks configured secrets and URLs in its output, and uses exclusive private snapshot files. It does not mutate queues, run FFmpeg, import the Preview application's ORM, or migrate its database.

On the ID host, after copying the collector there:

```bash
sudo python3 /tmp/collect.py arr-id \
  --postgres-container infinidysk-postgres \
  --id-url http://192.168.20.65:8080 \
  --reconcile-library \
  --output /tmp/media-triage-id-<timestamp>
```

The PostgreSQL mode reads only the two required `ConfigItems` values internally. For another deployment, supply `--arr-config-file` with the `arr.instances` JSON structure and `--id-key-file`; keep both private. The script does not discover credentials or assume PostgreSQL universally.

On the Preview host/container:

```bash
docker exec -i plex-previews python3 - preview \
  --db /config/plex_previews.db \
  --output /tmp/media-triage-preview-<timestamp> < collect.py
```

Use a new private output directory for each capture. Retrieve the JSON with an appropriate file-copy tool, keeping it private. `os.stat` failures on another host's paths mean the path could not be verified **there**, not that Plex or Arr has no file.

The collector records endpoint failures and queue pagination gaps. Fill these gaps before calling the audit comprehensive. Preview's database schema varies: the collector records available columns and does not treat omitted fields as zero or healthy.

## Live evidence outside the collector

- Arr `GET /api/v3/queue?pageSize=1000&includeMovie=true&includeEpisode=true&includeSeries=true`; paginate if needed. Use `GET movie/{id}`, `episode/{id}`, `moviefile/{id}`, `episodefile/{id}`, and per-series episode/file lists for reconciliation. Retain failed records that were not grabbed by Arr as a separate class: automatic failed-download handling may skip them.
- Manual-import **GET** with `folder=...&filterExistingFiles=false` can expose parsed files and rejections. It may probe media and take longer than ordinary metadata requests. Inspect files on disk too; a helper's extension allowlist can miss `.m4v`, `.mpg`, or `.ts`.
- ID SAB queue/history and current logs provide processing stage, status and fail reason. `GET /api/prefetch?includeCoverage=false` and `/api/get-health-check-queue?pageSize=100` expose damaged-source jobs and repair admission. Prefetch failures are historical records: deduplicate by `itemId`, check whether the `DavItem` still exists, and whether the library still points to it. Do not count replaced/deleted sources as current failures.
- `backend/Database/Models/DavItem.cs`: UUID, name, path, subtype, file/blob/NZB IDs, nullable history/grab links, repair flags, health timestamps. Join actual `DavItems` and `HistoryItems` schemas with validated UUIDs/parameters; avoid guessed columns. Preserve distinct ID item, history/download, blob and Arr file IDs.
- Watchdog: `/api/get-watchdog-entries?limit=...`, `WatchdogEntry` and `WatchdogRecoveryService`; corroborate replacement/import identity rather than equating queue completion with library recovery.
- Preview: `mediaitem` has rating key, status, error, cached path, current parts/hashes, progress and attempt time. Read configured `DB_PATH`, status counts and actual worker processes. Authenticate with the configured login; do not forge cookies or echo credentials. Compare API parts and the authoritative Plex data source when the preview row is stale.

An ID correlation can be collected with a narrow read-only query after validating UUIDs:

```sql
SELECT d."Id", d."Name", d."Path", d."SubType", d."FileSize",
       d."FileBlobId", d."NzbBlobId", d."HistoryItemId", d."ArrDownloadId",
       d."HealthRepairPending", d."LastHealthCheck", d."NextHealthCheck",
       h."JobName", h."Category", h."FailMessage"
FROM "DavItems" d
LEFT JOIN "HistoryItems" h ON h."Id" = d."HistoryItemId"
WHERE d."Id" = :validated_id;
```

Avoid querying all media blobs or downloading entire releases merely to identify a source.

Offline collector checks (no production access):

```bash
python3 .agents/skills/stuck-media-triage/scripts/test_collect.py
```
