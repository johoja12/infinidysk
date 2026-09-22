# Parallel NzbDav migration deployment notes

This file records the September 20, 2026 parallel InfiniDysk deployment used to
evaluate migration from the heavily diverged local NzbDav fork. It is an operator
record, not a claim that the old database or cache can be attached directly.

## Safety boundary

The existing NzbDav deployment remains authoritative. InfiniDysk has a fresh
PostgreSQL database, separate application data, separate WebDAV identity, separate
rclone container, and separate mount. Plex and the *Arr applications have not been
connected to InfiniDysk.

| Resource              | Existing NzbDav          | Parallel InfiniDysk        |
| --------------------- | ------------------------ | -------------------------- |
| Host                  | `192.168.20.65`          | `192.168.20.65`            |
| Frontend              | port `3003`              | port `3004`                |
| Application container | `nzbdav`                 | `infinidysk`               |
| PostgreSQL container  | `nzbdav-postgres`        | `infinidysk-postgres`      |
| Application data      | `/opt/nzbdav/config`     | `/opt/infinidysk/config`   |
| PostgreSQL data       | existing production path | `/opt/infinidysk/postgres` |
| rclone container      | `rclone-nzbdav`          | `rclone-infinidysk`        |
| Mount                 | `/mnt/remote/nzbdav`     | `/mnt/remote/infinidysk`   |
| rclone RC             | `5573`                   | `5574`                     |
| VFS cache             | production cache         | isolated 10 GiB maximum    |

The installed image is pinned to:

```text
ghcr.io/infinidysk/infinidysk@sha256:797295beb67ac57e75224bf6354d8be227d8882854cb315f2b50790a569880a2
```

## Deployed locations

- Application Compose: `/opt/docker/infinidysk/docker-compose.yml`
- Application secrets: `/opt/docker/infinidysk/secrets/` (root only)
- rclone Compose: `/opt/docker/rclone-infinidysk/docker-compose.yml`
- rclone configuration: `/opt/docker/rclone-infinidysk/config/rclone.conf`
- Public URL: `https://infin.sakhter.org`
- Frontend LAN URL: `http://192.168.20.65:3004`
- Backend WebDAV LAN URL: `http://192.168.20.65:8080` (trusted LAN only)
- Nginx Proxy Manager host: ID `61`, wildcard certificate ID `7`, HTTP backend
  `192.168.20.65:3004`, forced TLS, HTTP/2, WebSockets, and exploit blocking

The administrator username is `admin`; its password is stored only in
`/opt/docker/infinidysk/secrets/admin.env`. The original disposable
`migration-admin` account was removed through the supported
`RESET_ADMIN_PASSWORD` startup flow, and the reset flag was removed before the
replacement account was created. WebDAV, PostgreSQL, and provider
credentials must not be copied into issues, logs, commits, or support packs.

## Current deployed configuration

The following is the sanitized configuration observed on nuc-1 on September 20, 2026. Values marked as secrets live only on the host and are intentionally not
reproduced here.

### Application Compose project

The Compose project in `/opt/docker/infinidysk` contains two services:

| Setting         | `infinidysk`                                                              | `infinidysk-postgres`                               |
| --------------- | ------------------------------------------------------------------------- | --------------------------------------------------- |
| Image           | pinned InfiniDysk digest above                                            | `postgres:17-alpine`                                |
| Container name  | `infinidysk`                                                              | `infinidysk-postgres`                               |
| Restart policy  | `unless-stopped`                                                          | `unless-stopped`                                    |
| Published ports | host `3004` to container `3000`; `192.168.20.65:8080` to container `8080` | none                                                |
| Persistent bind | `/opt/infinidysk/config:/config`                                          | `/opt/infinidysk/postgres:/var/lib/postgresql/data` |
| Health check    | `curl -fsSL http://localhost:3000/healthz`                                | `pg_isready -U infinidysk -d infinidysk`            |
| Dependency      | waits for PostgreSQL health                                               | none                                                |

The application loads `secrets/app.env`, `secrets/webdav.env`, and
`secrets/providers.env`; PostgreSQL loads `secrets/postgres.env`. The deployed
files and their variable names are:

| File            | Variables (values are not committed)                                                                                                         |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| `app.env`       | `INFINIDYSK_IMAGE`, `DATABASE_PROVIDER`, `DATABASE_CONNECTION_STRING`, `PUID`, `PGID`, `TZ`, `NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED` |
| `postgres.env`  | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`                                                                                          |
| `webdav.env`    | `WEBDAV_USER`, `WEBDAV_PASSWORD`                                                                                                             |
| `providers.env` | `NZBDAV_CONFIG__USENET__PROVIDERS`, `NZBDAV_CONFIG__API__CATEGORIES`                                                                         |
| `admin.env`     | `ADMIN_USERNAME`, `ADMIN_PASSWORD`; operator record only, not loaded by Compose                                                              |

The secrets directory is mode `0700` and each `.env` file is mode `0600`, owned
by `root:root`. Always include the protected environment file when operating the
application project:

```bash
cd /opt/docker/infinidysk
sudo docker compose --env-file secrets/app.env config --quiet
sudo docker compose --env-file secrets/app.env up -d
```

The backend WebDAV port is deliberately published only as
`192.168.20.65:8080:8080`. Do not replace this with a wildcard (`8080:8080` or
`0.0.0.0:8080:8080`) mapping and do not add an IPv6 mapping: rclone is the
only intended direct consumer and stays on the trusted Docker/LAN path. Browser
and public reverse-proxy traffic continue through frontend port `3004`.

`RESET_ADMIN_PASSWORD` is deliberately absent from the running container. Add it
only as a temporary one-shot override for the documented recovery flow, then
recreate from the base Compose file immediately afterward.

### Isolated rclone project

`rclone-infinidysk` uses `sakhter86/rclone:latest`, restart policy
`unless-stopped`, `/dev/fuse`, `SYS_ADMIN`, and `apparmor:unconfined`. Its
configuration is read-only at `/config`; cache and logs are isolated beneath
`/opt/docker/rclone-infinidysk`. The mount bind uses `rshared` propagation.

| Setting       | Deployed value                                                  |
| ------------- | --------------------------------------------------------------- |
| Container     | `rclone-infinidysk`                                             |
| Image         | `sakhter86/rclone:latest`                                       |
| Compose file  | `/opt/docker/rclone-infinidysk/docker-compose.yml`              |
| rclone remote | `infinidysk:` (`webdav`, vendor `other`)                        |
| WebDAV target | `http://192.168.20.65:8080` (trusted LAN backend)               |
| Credentials   | protected in `/opt/docker/rclone-infinidysk/config/rclone.conf` |
| Host mount    | `/mnt/remote/infinidysk`                                        |
| VFS cache     | `/opt/docker/rclone-infinidysk/cache`, maximum `10G`            |
| Logs          | `/opt/docker/rclone-infinidysk/logs/rclone.log`                 |
| RC endpoint   | `127.0.0.1:5574`, unauthenticated but loopback-only             |

The sanitized remote definition is:

```ini
[infinidysk]
type = webdav
url = http://192.168.20.65:8080
vendor = other
user = <stored only on host>
pass = <stored only on host>
```

The active mount command is equivalent to:

```text
rclone mount infinidysk: /mnt/remote/infinidysk
  --config=/config/rclone.conf
  --allow-other --allow-non-empty --links
  --vfs-cache-mode=full --vfs-cache-max-size=10G --vfs-cache-max-age=24h
  --vfs-read-chunk-size=128M --vfs-read-ahead=128M --buffer-size=64M
  --cache-dir=/cache --dir-cache-time=1m --attr-timeout=1m
  --vfs-fast-fingerprint --timeout=10m --contimeout=2m
  --low-level-retries=3 --retries=2 --retries-sleep=1s
  --rc --rc-addr=:5574 --rc-no-auth
  --log-level=INFO --log-file=/logs/rclone.log
```

RC is published only on `127.0.0.1:5574`. The container has no Docker socket,
restart sidecar, or dependency on Plex, Arr, or the production NzbDav mount.

### Reverse proxy

Nginx Proxy Manager proxy host ID `61` currently has this non-secret
configuration:

| Setting                        | Value                       |
| ------------------------------ | --------------------------- |
| Domain                         | `infin.sakhter.org`         |
| Upstream                       | `http://192.168.20.65:3004` |
| Certificate                    | ID `7`                      |
| Force SSL / HTTP2 / WebSockets | enabled                     |
| Block common exploits          | enabled                     |
| Asset caching                  | disabled                    |
| Access list                    | public (`0`)                |

## Configuration decisions

- PostgreSQL 17 is private to the InfiniDysk Compose network.
- WebDAV Basic Auth uses `WEBDAV_USER` and `WEBDAV_PASSWORD`. A database account
  with type `WebDav` does not configure the WebDAV endpoint.
- The five existing Usenet provider definitions and the existing category list are
  supplied through a root-only `NZBDAV_CONFIG__...` environment file. Neither
  database was modified to copy settings.
- InfiniDysk's segment cache is disabled during discovery. The old native-cache
  bytes are not portable because the two implementations use different identities
  and storage semantics.
- The test rclone stack has no Docker socket and no dependent-service restart hook.

## Verification completed

- InfiniDysk and its PostgreSQL container are healthy with zero restarts.
- Frontend `/healthz` returns HTTP 200.
- Authenticated WebDAV PROPFIND returns HTTP 207.
- `/mnt/remote/infinidysk` is an independent `fuse.rclone` mount.
- rclone RC reports no fatal or retry errors.
- A retained NzbDav NZB completed successfully in InfiniDysk.
- The pilot produced two leaves with the same combined size as the source
  (`31,371,214` bytes).
- A 1 MiB read through the new mount completed successfully.
- The existing NzbDav endpoint and `/mnt/remote/nzbdav` remained mounted and
  readable; no Plex or Arr consumer was switched to the parallel mount.

The container currently emits a non-fatal Alpine warning that
`libgssapi_krb5.so.2` is missing. Database maintenance and both health checks finish
successfully after the warning.

## Import discovery

The current NzbDav database contains 234 completed and 121 failed history records.
All 355 have non-empty XML NZB blobs (about 233 MiB combined). The blob store also
contains 55,104 files no longer referenced by current history (about 49 GB); a
random 1,000-file sample was entirely XML-like NZB data.

The existing Plex library contains 41,274 symlinks. Every link targets a current
legacy NzbDav `/.ids/...` DavItem ID, but only 153 linked IDs are descendants of
the retained completed-history set. Therefore a bulk replay of current history
would leave 41,121 existing library targets unresolved.

The pilot also demonstrated that replay generates new canonical IDs and may
normalize filenames differently. Filename matching alone is not a safe migration
key.

## Required migration approach

The approved implementation creates a separate local `/mnt/plex2` tree; it does
not rewrite `/mnt/plex` and does not add a compatibility resolver to the legacy
mount.

1. Back up InfiniDysk `/config` and PostgreSQL before deploying the importer. Its
   full-batch ledger migration is additive and auto-applies at startup.
2. Freeze a no-follow inventory of `/mnt/plex` and a private orphan-blob input
   list. Keep the run directory `0700` and generated evidence `0600`.
3. Build the resumable SQLite orphan catalogue. Classify byte-identical duplicate,
   distinct ambiguity, corrupt, missing, direct, and archive evidence without using
   filenames as identity.
4. Require the checksummed dry run to recover at least 90% of source links before
   exporting. The default batch bounds are 250 releases and 4 GiB of NZB payload;
   lower them when queue pressure requires it.
5. Import batches in order through InfiniDysk's normal queue/history path. Pause
   and resume the same package when needed; reconnects with the same digest are
   idempotent. Do not advance until the current batch is terminal, reconciled,
   exact, applied, validated, and acknowledged.
   The package `PayloadPath` is an internal checksummed locator, not a release name.
   New exports carry authoritative `SourceFileName` and `SourceJobName` values from
   legacy history, or a deterministic name derived from the legacy content path for
   recovered orphans. Compatible older packages remain readable but must be
   regenerated before a name-preserving import.
6. Apply each exact-only plan create-only beneath `/mnt/plex2`, with `/mnt/plex` as
   the required source-drift fence and `/mnt/remote/infinidysk` as the target root.
   Retain the checksummed plan, apply journal, and bounded-read validation report.
7. Run a fresh delta inventory after the initial batches, process added links, and
   generate aggregate coverage using the live final source snapshot as denominator.
8. Verify HEAD, range, seek, six-file first/repeat speed evidence, and representative
   playback manually. Use direct backend port `8080` for throughput claims.
9. Roll back only with the matching apply journal. Do not copy old native-cache
   files or delete imported data/evidence as part of symlink rollback.

Exact commands and artifact semantics are in
`docs/guides/nzbdav-migration.md`. The setup wizard remains unchanged: this is an
advanced migration workflow and adds no `ConfigKeys` option.

No bulk import, library rewrite, Plex/*Arr switch, or native-cache copy has occurred.
`/mnt/plex2` must remain unregistered with both Plex and every Arr application
through the full recovery and manual end-to-end validation period.

Any Phase B replacement is restricted to the exact migration-ledger IDs in the
dedicated `migration-*` categories and the links owned beneath `/mnt/plex2`. The
legacy NzbDav deployment, `/mnt/plex`, Plex, Arr, unrelated InfiniDysk history, and
the reusable migration tool installation are outside the deletion boundary. Recheck
the live IDs, row/file counts, and symlink ownership immediately before cleanup.

## Operator checks

```bash
# Application and database health
sudo docker ps --filter name=infinidysk
curl -fsS http://127.0.0.1:3004/healthz

# The backend must be available only on the trusted LAN address.
ss -ltn '( sport = :8080 )'
curl -fsS http://192.168.20.65:8080/health
# An unauthenticated WebDAV request must remain rejected.
curl -o /dev/null -sS -w '%{http_code}\n' http://192.168.20.65:8080/view/

# Independent mount and rclone state
findmnt -T /mnt/remote/infinidysk
timeout 10 ls /mnt/remote/infinidysk/content >/dev/null
curl -fsS -X POST http://127.0.0.1:5574/core/stats | jq .

# Public proxy
curl -fsS https://infin.sakhter.org/healthz

# Confirm production remains separate
findmnt -T /mnt/remote/nzbdav
curl -fsS http://127.0.0.1:3003/ >/dev/null
```

## Rollback

Rollback stops only `rclone-infinidysk`, detaches only
`/mnt/remote/infinidysk` if it is verified stale, then stops the InfiniDysk Compose
project. Retain all data and secret directories for diagnosis. Do not delete data,
detach `/mnt/remote/nzbdav`, or restart production consumers as part of this rollback.
