# Rust persistent library checkpoint — 2026-11-04

Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust baseline: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
Generated local data only; production and the 1.x source remain unchanged.

## Results

- Final frozen-source workspace: **401 passed, zero failed, six ignored**, including
  real PostgreSQL 17 and SQLite. All doc builds pass. Formatting, workspace/all-targets
  Clippy, generated API schema and TypeScript/Vite production build pass.
- Shared database tests cover atomic snapshot replacement, rollback of a rejected
  replacement, failure retaining the old scope/index, dangling sources, persisted
  restart state and same-millisecond concurrent request generations.
- Scanner tests cover recognized pointer identity, protected roots/directories,
  cancellation, directory symlinks, cache exclusions and scope changes. A valid STRM
  grown beyond the byte limit after a stale stat is rejected.
- The actual app HTTP suite converts pointers both ways, requests a scan, browses a
  group, opens indexed file details and rejects an unconfigured root. Authentication
  and OpenAPI routes are checked separately in the app suite.
- VFS tests prove exact native-cache byte attribution for warm seeks, with no provider
  BODY requests. Metrics draining does not consume the lightweight burst history.
- Chromium checks use the actual configured local API and UI, with no simulated rows:
  empty state, root scan, concurrent refresh generations, grouped browsing, details,
  source revision, current coverage and a 390px layout. Four HTTP handles group together;
  three cached range reads attribute exactly 771 bytes to native cache. No page errors.
- Renaming the fixture library directory triggers a real scan failure. Previous groups
  remain visible as stale. Restoring it and requesting a refresh recovers the index.
- The same configured canary passes a full 8,388,608-byte SHA-256 through WebDAV and
  the kernel mount, three kernel seeks, and kernel native-cache attribution.
- Both privileged FUSE tests pass separately. All three original C# writer cache tests
  pass separately, including full import/shared VFS and partial coverage preservation.
  The remaining ignored local-sample NZB test was not run; it requires an external sample.

The configured-canary binary SHA-256 is
`aa25b6f09fa23bde9237434b8b7df69e8fb5e121c1c705ed449c2f13fa448b02`.
Its receipt is local under `/tmp/infinidysk-fork-local-*`; browser evidence is
`/tmp/infinidysk-rust-browser/catalogue-validation.json` and `catalogue-mobile.png`.
These are generated-fixture results, not live Plex/provider, decoder or performance
qualification. Earlier library checkpoints passed 399 tests; two final storage-safety
checks bring the final run to 401. Initial compile/lint issues were corrected before
qualification. No test timeout was loosened or failure skipped.

Only obsolete executable artifacts in this task's Rust target directory were removed
to free disk space. No source, production state or unrelated worktree was cleaned.
The disposable canary was stopped and its mount released after acceptance.

## Bounds, upgrade and remaining work

The first scanner caps each root at 50,000 links, 150,000 filesystem entries, 16 MB of
path/pointer text and a 25-second pass. A larger root requires durable paged staging;
exceeding a bound retains the last complete index. An individual blocked filesystem
operation may outlive the API/task timeout, but retains its blocking-I/O permit and
observes cancellation when it returns. At most two such pointer operations can remain
outstanding. Read-side browse/detail APIs do not touch NAS metadata or media payloads.

Migration 0014 is additive on both database engines. **Back up `/config` before any
upgrade.** Existing `imports.library_dirs` selects roots; there is no new setting or
setup-wizard field. New installations have no roots, so indexing starts only after
configuration. Indexing never rewrites links, warms media, adopts original cache roots
or establishes authority to delete/regrab a source.

Plex metadata matching, verified viewer correlation, larger-root staged scans, the
remaining prefetch source/budget/policy/history work, full health sweeps, detached
repair controls and broader fork operational migration remain open. The PR remains
a draft; this checkpoint does not authorize deployment or production cutover.

Use the pinned toolchain with `CARGO_INCREMENTAL=0`, `CARGO_PROFILE_DEV_DEBUG=0`,
`CARGO_PROFILE_TEST_DEBUG=0`, `TOKIO_WORKER_THREADS=4` and `-j3`. Run:

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -j3 -- -D warnings
cargo test --workspace --locked --no-fail-fast -j3
npm --prefix ui run api
npm --prefix ui run build
```

Set `INFINIDYSK_TEST_POSTGRES_URL` only to a disposable local administrator connection.
Kernel and original-writer fixture checks use the earlier cache receipt's setup.
