# InfiniDysk Backend LAN Exposure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Point the isolated rclone mount directly at InfiniDysk's backend over the trusted LAN without publishing the backend to untrusted interfaces.

**Architecture:** Bind container port 8080 to the one trusted host address (`192.168.20.65`) and update only the isolated rclone remote URL. The browser and public reverse-proxy path remains frontend port 3004. Runtime work is host-local under `/opt/docker`; the repository records the resulting topology and safe validation commands.

**Tech Stack:** Docker Compose, rclone WebDAV/FUSE, Linux socket inspection, Markdown.

---

### Task 1: Document the LAN-only backend topology

**Files:**
- Modify: `DEPLOYMENT-NOTES.md`

- [ ] **Step 1: Update the application port mapping and rclone endpoint records**

Add the exact mapping and endpoint to the deployed-configuration tables:

```markdown
| Published ports | host `3004` to container `3000`; `192.168.20.65:8080` to container `8080` | none |
| Backend WebDAV LAN endpoint | `http://192.168.20.65:8080` |
```

Replace the rclone WebDAV target and sanitized remote URL with `http://192.168.20.65:8080`. State explicitly that port 8080 is neither wildcard-bound nor IPv6-published.

- [ ] **Step 2: Add reproducible safety checks**

Document these checks without credentials:

```bash
sudo docker compose --env-file secrets/app.env config --quiet
sudo docker compose config --quiet
ss -ltn '( sport = :8080 )'
timeout 10 ls /mnt/remote/infinidysk/content >/dev/null
curl -fsS -X POST http://127.0.0.1:5574/core/stats | jq .
```

Expected: Compose validates, `ss` has `192.168.20.65:8080` only, the isolated mount is readable, and rclone has no fatal/retry errors.

- [ ] **Step 3: Commit the documentation**

```bash
git add DEPLOYMENT-NOTES.md
git commit -m "chore(docs): document trusted backend WebDAV endpoint"
```

### Task 2: Apply the narrowly scoped host configuration

**Files:**
- Modify: `/opt/docker/infinidysk/docker-compose.yml`
- Modify: `/opt/docker/rclone-infinidysk/config/rclone.conf`

- [ ] **Step 1: Confirm the intended host files and existing listener state**

```bash
test -f /opt/docker/infinidysk/docker-compose.yml
test -f /opt/docker/rclone-infinidysk/config/rclone.conf
ss -ltn '( sport = :8080 )'
```

Expected: both files exist and port 8080 is unused before publication.

- [ ] **Step 2: Bind the backend only to the trusted LAN address**

In the `infinidysk` service `ports:` list, add exactly:

```yaml
- "192.168.20.65:8080:8080"
```

Do not add `8080:8080`, `0.0.0.0:8080:8080`, an IPv6 mapping, or a PostgreSQL/RC port mapping.

- [ ] **Step 3: Point only the isolated rclone remote at the backend**

In `[infinidysk]`, replace only:

```ini
url = http://192.168.20.65:3004
```

with:

```ini
url = http://192.168.20.65:8080
```

Preserve every credential and all other rclone settings verbatim.

- [ ] **Step 4: Validate Compose before recreation**

```bash
cd /opt/docker/infinidysk
sudo docker compose --env-file secrets/app.env config --quiet
cd /opt/docker/rclone-infinidysk
sudo docker compose config --quiet
```

Expected: both commands exit 0.

- [ ] **Step 5: Recreate only the two parallel services**

```bash
cd /opt/docker/infinidysk
sudo docker compose --env-file secrets/app.env up -d --force-recreate infinidysk
cd /opt/docker/rclone-infinidysk
sudo docker compose up -d --force-recreate rclone-infinidysk
```

Do not recreate NzbDav, Plex, Arr, PostgreSQL, or any unrelated service.

- [ ] **Step 6: Verify binding, authentication boundary, and mount health**

```bash
ss -ltn '( sport = :8080 )'
curl -fsS http://192.168.20.65:8080/health
curl -o /dev/null -sS -w '%{http_code}\n' http://192.168.20.65:8080/view/
findmnt -T /mnt/remote/infinidysk
timeout 10 ls /mnt/remote/infinidysk/content >/dev/null
curl -fsS -X POST http://127.0.0.1:5574/core/stats | jq .
findmnt -T /mnt/remote/nzbdav
```

Expected: 8080 is bound only to `192.168.20.65`, health succeeds, unauthenticated WebDAV is rejected, the isolated mount is `fuse.rclone` and readable, rclone reports no fatal/retry errors, and the authoritative mount remains intact.

### Task 3: Verify repository delivery

**Files:**
- Modify: `DEPLOYMENT-NOTES.md`
- Create: `docs/superpowers/plans/2026-09-20-infinidysk-backend-lan.md`

- [ ] **Step 1: Check the documentation diff**

```bash
git diff --check main...HEAD
git diff -- DEPLOYMENT-NOTES.md
```

Expected: no whitespace errors and only the LAN-only endpoint/documentation changes.

- [ ] **Step 2: Commit the implementation plan**

```bash
git add docs/superpowers/plans/2026-09-20-infinidysk-backend-lan.md
git commit -m "chore(agents): plan backend LAN exposure"
```

