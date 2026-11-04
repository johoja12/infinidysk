# Automatic local repair and native-copy retention

Qualified Rust source: `1bd81a67987cfb312f8e9dfc13d8db625b8021c2`.
The 1.x specification remains unchanged at
`9c104b5961abe67c14e6565932e48ea340f1dbd3`.

## Result

- Full workspace: **546 passed, zero failed, six ignored**, including actual local
  PostgreSQL 17 and SQLite. All-target workspace Clippy passes with warnings denied.
- The three original C# writer interoperability tests pass separately. They prove
  copied 1.x sparse/chunked/partial cache data, multipart generations and original
  repair-revision matching. They do not authorize sharing a writable legacy root.
- Both privileged kernel FUSE tests pass separately, including native cached bytes.
- The optional external NZB corpus remains unavailable. It is not counted as passed.

The six default ignored cases are the three separately executed C# cases, two separately
executed FUSE cases and the unavailable external corpus. No live provider, Plex account,
production deployment, decoder acceptance or controlled performance percentage is claimed.

## Worker coverage

Eight application integration tests use real SQLite, a generated loopback NNTP provider,
independently generated par2cmdline recovery packets, the authoritative repair ledger,
the shared decoded cache and VFS reads/seeks. They cover:

- Durable playback urgency and actual bounded reconstruction before replacement.
- Complete-article publication and fresh health verification; playback and seeking
  still return exact bytes after providers are removed.
- Active-reader deferral before provider work, then admission after last close.
- Provider outage deferral without deleting the source or losing urgency.
- Healthy original posts without manufacturing a repair revision.
- Restart/readback of uncertain publication while new attempts are disabled,
  without repeating provider discovery or publication.
- Source changes while publication waits behind the mutation barrier.
- Missing-slice cooldown without repeated provider reads and foreground admission.

Main-database tests exercise attempt admission/CAS, bounded capacity, priority/fairness,
publication recovery and atomic terminal outcome plus urgent fresh-check admission on
both SQLite and PostgreSQL. Shared-reader and source validation remain inside publication
admission. Existing patches stay authoritative when new repair attempts are disabled.

## Reproduced maintenance race

A deterministic test first reproduced nested write admission deadlocking behind a pending
exclusive maintenance pause. Internal last-reader reconciliation now reuses the existing
permit. The same test and all eight overlay tests pass after the fix.

## Native retained-copy protection

Native storage tests now total 19. Complete copies retained for damaged sources gain a
durable automatic pin before storage verification. Missing mounts/uncertain I/O keep that
pin. Conclusive corruption or partial coverage cannot pass complete-copy verification.
Manual pins survive automatic protection cleanup. Source removal/revision maintenance
releases only automatic protection; explicit unpinning permits eviction. Pin admission
holds a generation lease until SQLite confirms the update.

## Runtime and UI

The authenticated application test starts with opt-in PAR2 enabled, opens an exclusive
repair ledger, exposes bounded history, rejects unauthenticated inspection and shuts down
cleanly. Config tests cover bounds, persistence and environment-owned values.

Generated OpenAPI types and the TypeScript/Vite build pass. Chromium checks against the
rebuilt embedded UI exercise settings save/reload, all three repair limits, first-enable
restart notice, empty attempt history and 390px layout with no page errors. The browser also checks the cache protection explanation at mobile width.
The final embedded canary SHA-256 is
`20e1f936c1e8cd5c5bae110be3cc4e1d2be09998ea1802d5f1d977c40827846d`.
These UI
checks use a disposable generated-source instance and do not simulate a production repair.

Reproduction logs are task-local under `/tmp/rust-fork-repair-completion-*`, with focused
worker cases under `/tmp/rust-fork-repair-worker-safety.log` and the reproduced/fixed lock
case under `/tmp/rust-fork-reader-pause-{repro,fixed}.log`.

## Remaining port scope

This closes the opt-in automatic repair worker increment. The overall fork migration
remains incomplete: Smart Prefetch hub/library sources and full wire/lead/minimum/finish
policies/history, larger durable catalogue walks and verified Plex playback attribution,
offline cache catalogue reconstruction/detailed traffic diagnostics, and typed/operational
legacy migration still need implementation and qualification. See the capability inventory
in `../fork-v2-migration.md`. Back up config, including `repair-v2`, before upgrading.
