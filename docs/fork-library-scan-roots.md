# Additional library scan roots

**Since:** Rust 2.x fork migration branch (unreleased).

Settings → Library → Additional scan folders includes separate folders in catalogue
browsing, grouped video lists, file details, Plex metadata matching and imported-pointer
warming. The default is empty. Each folder has its local path and an optional path as
Arr sees it; an empty Arr path uses the local path.

These folders are read-only discovery sources. Library conversion, STRM rewriting
and dead-link cleanup operate on managed library folders. Orphan-source cleanup reads
both managed and additional folders so a source referenced only by an additional
folder is retained. An unreadable folder prevents a complete cleanup census.

Source replacement requires a current exact Arr file path under a managed library
mapping. Additional folders cannot grant replacement permission. File details disable
the repair action for sources lacking a managed mapping; the API enforces the policy
and the coordinator checks it again immediately before each external mutation.
Automatic deletion of unowned sources is suspended while additional roots exist;
those cases require attention. Read checks and warming remain available.

STRM pointers in additional folders are never rewritten. Replaced signing keys and
the accepted legacy key form therefore remain valid while additional roots are
configured. The Library status does not advertise an automatic retirement date in
that state. Explicit key revocation remains an administrator action.

Paths must be absolute, have no traversal or control characters and stay separate
from managed library roots, mount/output/cache storage and other scan roots. Neither
local nor Arr mappings may overlap a managed mapping. Filesystem roots are rejected.
At most 64 additional roots may be configured.

The corresponding setting is `imports.scan_dirs`, for example:

```json
[{"path":"/media/additional","arr_path":"/arr/additional"}]
```

`INFINIDYSK__IMPORTS__SCAN_DIRS` accepts this JSON array and remains authoritative;
the Settings UI cannot overwrite an environment-owned value. Changes apply without
a restart and invalidate affected catalogue scopes. Explicit refresh requests take
priority; unfinished root work receives a short follow-up interval while idle.

This is an optional advanced setting for existing libraries. It adds no new-install
wizard requirement and does not automatically rewrite imported files, activate legacy
migration journals or share a writable cache with 1.x.
