# Read-only library roots and aggregate qualification — 2026-11-04

Rust base: `0156be60` on `feat/rust-v2-fork-migration`.
Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

Additional `imports.scan_dirs` roots participate in catalogue/grouped browsing,
file details and exact indexed Plex metadata matching. They are excluded from
conversion, STRM rewriting and dead-link cleanup. Orphan cleanup includes their
references, so scan-only sources are retained. Replaced/legacy signing keys are
not automatically retired while these folders cannot be rewritten.

Repair policy distinguishes display inspection, an actual Arr path, and unowned
source deletion. Display records cannot grant destructive permission. Every Arr
mutation requires a current managed path, and its durable pending checkpoint
rechecks the policy. Automatic unowned deletion is disabled while additional roots
exist. File details expose nullable local eligibility and the UI disables unavailable
replacement actions; the API returns an actionable 400 for a rejected request.
Existing exact Arr root/file/source checks remain required.

The setting defaults to empty, validates local/Arr overlap and protected storage,
rejects root/traversal/control paths and honors the authoritative environment value.
It applies live. This advanced existing-library option has no new-install wizard
impact. The [operator guide](../fork-library-scan-roots.md) records the unreleased
introducing line and effects on cleanup/key retention.

Catalogue scheduling prioritizes explicitly requested roots. After a root slice,
work receives a five-second follow-up; idle polling remains one minute. This fixes
an observed delay where an automatic root rescan consumed the sole wakeup before
an explicitly requested second root was scanned. Large-root durable staging remains
an independent open capability; existing complete-index bounds are unchanged.

Validation of this frozen code, including the preceding sweep and import-lane fixes:

- **463 workspace tests passed, zero failed, six ignored**, using SQLite and actual
  PostgreSQL. The formerly intermittent import regression passes with its response
  held until the other job completes. All-target Clippy, formatting, generated API,
  TypeScript/Vite and native canary builds pass.
- Original C# writer fixtures: **three additional tests passed** for sparse/chunked,
  stored/encrypted multipart and partial cache coverage; original generation/repair
  provenance; full import; and VFS reads without providers. The two suites ran
  sequentially against the same immutable fixture receipt.
- **Two additional privileged kernel tests passed**, covering ordinary FUSE reads
  and native-cache-backed FUSE reads. No global FUSE configuration was changed.
- Library API/filesystem integration proves read-only pointers survive both conversion
  directions and key rotation, scan-only repair is rejected without an Arr write,
  and signing keys have no automatic retirement date in this mode. Maintenance proves
  scan-only references prevent orphan removal and dead-link cleanup leaves additional
  root pointers alone. A coordinator regression changes permission between preflight
  and the pending checkpoint and proves no Arr mutation is dispatched.
- Actual configured Rust app + Chromium + generated loopback NNTP: settings saved in
  the UI, root scanned, group/file opened, repair disabled/API rejected, manual warming
  completed, and full-file/three seek reads matched their hashes through the shared
  cache. The pointer stayed unchanged. A visible 390 px catalogue has no horizontal
  overflow or page errors; screenshot inspected after content loaded.
- Browser receipt: `/tmp/infinidysk-rust-browser/readonly-roots-validation.json`.
  Canary SHA-256: `c76e7a424f9ef4761d5b060bd9699c06942654fa459b973850ccd91b8924ec44`.
- Logs: `/tmp/rust-fork-readonly-roots-{workspace,clippy,api,ui,csharp-import,csharp-cache,kernel}.log`.

The workspace count retains six ignored cases. Three actual-writer cases and two
privileged kernel cases are qualified separately; the optional local NZB corpus is
not supplied. This evidence does not certify live Plex/providers, production rollout
or the remaining feature groups. No production service or 1.x source was changed.
Existing additive migration requirements remain: **back up `/config` before upgrading**.
