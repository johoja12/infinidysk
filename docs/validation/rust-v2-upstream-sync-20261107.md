# Rust 2.x upstream sync — 2026-11-07

## Source identity

Merge upstream `main` at `e3e0507366d9e17abda275018644d3aa92f069f2`
(including `v2.0.0-beta.6` and the subsequent Arr webhook fix) into `feat/rust-v2-fork-migration` at
`fee093cf34c84f137b622db34dfac7909ab70a01`. This includes all 32 upstream
commits since the previously included `d4f5daf5991447d2726a5b22d9232c5d9c265c94`.

The sync includes automatic PostgreSQL 1.x import, configurable database/file
locations, Sonarr v5, Chaptarr replacing Readarr, Lidarr improvements, safer repair
ownership and provider-outage handling, optional FUSE, database cleanup and queue
performance fixes, quieter UI refreshes, backup settings, SSO display names and
Linux release archives. The latest webhook fix saves initially unreachable Arr
webhooks by creating them disabled before enabling their events.

## Fork integration

- Keep the fork repair coordinator, reader and publication guards, native cache,
  Plex, warming, library catalogue and migration evidence.
- Retain durable completed-pointer removal across restarts. Upstream's temporary
  in-memory hiding is superseded by the existing fork database tombstones.
  Both delivery methods preserve source items and history.
- Adapt tracked regrab and original-download provenance lookup to upstream API
  sessions, including Sonarr v5. Keep checkpoint-before-dispatch and uncertain
  response reconciliation. Extend tracked repair to Chaptarr book files.
- Keep the fork's verified, bounded PostgreSQL snapshot exporter in a separate
  module, re-exported through the existing public API, alongside upstream's
  automatic PostgreSQL staging importer.
- Copy the optional fork `ArrRegrabRequests` table during automatic PostgreSQL
  staging. Import its unknown fields as immutable evidence and retain repair
  holds. Stock upstream installations need no such table.
- Number the upstream health-summary index migration as `0026` on both database
  backends. The fork already owns migration `0009`; its checksums stay intact.
- Regenerate the combined OpenAPI types and keep fork repository identity in the
  image labels and multi-platform manifest annotations.

Back up `/config` (or the configured data directory) before upgrading. The new
index migration is additive. External cache payloads/catalogues, repair ledgers
and metrics require matching backups. Never share writable cache roots between
1.x and Rust.

## Validation

All runtime fixtures use local
loopback services and a disposable PostgreSQL 17 instance. This sync does not
qualify production providers, privileged kernel FUSE, live Plex/Arr, performance,
release archives, arm64 packaging or production migration/cutover. Earlier runtime
qualification receipts apply only to their recorded revisions.


- `cargo fmt --all --check`: passed.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: passed.
- `cargo test --workspace --locked --no-fail-fast`: 782 passed, zero failures,
  six ignored. PostgreSQL cases used a disposable PostgreSQL 17 instance.
- The ignored cases require separate real C# writer, local NZB or kernel FUSE
  fixtures. Unprivileged application tests emitted FUSE permission warnings;
  these are not privileged mount qualification.
- Focused upgrade and health suites also passed (19 and 20 tests).
- API generation and TypeScript/Vite build: passed. Vite reports a large-chunk
  warning.
- Python operations tests: 27 passed. All four systemd templates and the mocked
  Discord announcement script checks passed.
- Conflict-marker and whitespace checks: passed.

An earlier run had a native-cache read miss during concurrent compilation.
The unchanged restart/partial-coverage test and all 21 cache-store tests passed
in the final full run. No production performance or provider claim is made.
