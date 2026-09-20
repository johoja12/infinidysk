# InfiniDysk Backend LAN Exposure Design

## Goal

Expose the parallel InfiniDysk backend directly on nuc-1's trusted LAN interface so local-network rclone instances can bypass the frontend Node proxy, while keeping the backend unavailable through other host interfaces.

## Current state

- nuc-1's trusted LAN address is `192.168.20.65/23` on `enp88s0`.
- The parallel InfiniDysk frontend is published as host port `3004` to container port `3000`.
- Host port `8080` is unused.
- `rclone-infinidysk` currently targets `http://192.168.20.65:3004` and mounts at `/mnt/remote/infinidysk`.
- The parallel mount has no Plex or Arr consumers and is isolated from authoritative NzbDav.

## Change

1. Add `192.168.20.65:8080:8080` to the `infinidysk` service's published ports in `/opt/docker/infinidysk/docker-compose.yml`.
2. Change only the URL in `/opt/docker/rclone-infinidysk/config/rclone.conf` to `http://192.168.20.65:8080`; preserve credentials and every other rclone setting.
3. Validate both Compose projects before recreation.
4. Recreate only the `infinidysk` application container, wait for health, and confirm the backend listener is bound specifically to `192.168.20.65:8080`.
5. Recreate only `rclone-infinidysk`, then verify the independent FUSE mount and RC statistics.
6. Update `DEPLOYMENT-NOTES.md` with the backend LAN endpoint, published-port mapping, and direct rclone target.

## Security boundary

- Do not publish backend port `8080` on `0.0.0.0` or IPv6.
- Do not expose PostgreSQL or rclone RC beyond their current boundaries.
- Do not print or modify WebDAV, database, provider, or administrator credentials.
- Do not change Nginx Proxy Manager; browser traffic remains on frontend port `3004`.
- Do not restart or alter authoritative NzbDav, Plex, Arr, or `/mnt/remote/nzbdav`.

## Verification

- Before mutation, confirm `192.168.20.65:8080` is closed and unused.
- Run `docker compose --env-file secrets/app.env config --quiet` for the application project and `docker compose config --quiet` for the rclone project.
- Confirm `infinidysk` and `infinidysk-postgres` are healthy after application recreation.
- Confirm `ss` shows `192.168.20.65:8080` and does not show `0.0.0.0:8080` or `[::]:8080`.
- From another LAN host, confirm backend health succeeds and unauthenticated WebDAV access is rejected.
- Confirm the active redacted rclone config targets port `8080`, `/mnt/remote/infinidysk` remains `fuse.rclone`, and a bounded directory read succeeds.
- Confirm rclone RC reports no fatal or retry errors.
- Observe frontend traffic/logs long enough to show the recreated rclone is no longer sending requests to port `3004`; the UI warning itself may remain visible for its documented 30-minute observation window.
- Recheck the authoritative `/mnt/remote/nzbdav` mount without restarting it.

## Rollback

If backend publication or rclone recovery fails, restore the two original files, recreate only the affected parallel containers, and verify the prior frontend-based mount returns. Do not detach or restart authoritative NzbDav consumers during rollback.

## Repository delivery

Commit the documentation and implementation artifacts with `chore(docs)` / `chore(agents)` commits, push a dedicated branch, and open a PR to `main`. The live production change and repository handoff are reported separately.
