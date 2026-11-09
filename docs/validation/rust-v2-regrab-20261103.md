# Rust durable repair checkpoint — 2026-11-03

Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust baseline: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
This is local generated-fixture validation. No production Arr, provider, library,
Plex or deployment was touched.

## Behavior and evidence

- Arr loopback tests prove lost DELETE acknowledgement, original file still visible,
  accepted search with a failed response, replacement races and rejected checkpoints.
  Uncertain external writes are observed rather than blindly replayed.
- A health integration test closes and reopens the actual SQLite database between
  attempts. It retains original identity after the Arr forgets its old file, observes
  exactly one deletion and one search, and verifies the journal excludes the API key.
- SQLite and actual PostgreSQL 17 share CAS, detached-journal retention, user-request
  priority and concurrent same-priority generation checks. The PostgreSQL fixture is
  loopback-only; `INFINIDYSK_TEST_POSTGRES_URL` explicitly enables that test.
- Atomic reader/repair admission is exercised directly and through VFS for FUSE and
  WebDAV origins. Stale nodes cannot reopen a removed source. WebDAV maps a repair
  admission conflict to 503 with Retry-After; FUSE uses EAGAIN.
- The authenticated journal route and its OpenAPI contract are checked by the actual
  application HTTP suite. Unauthenticated access fails and a missing intent is null.
- Both privileged kernel FUSE checks pass, including native-cache reads and reads from
  already-open handles after the database is closed.
- All three original C# writer interoperability checks pass separately using the
  generated `20261103c` fixture. The full importer and shared VFS still serve copied
  1.x sparse/chunked/encrypted bytes without providers; partial coverage stays partial.
- Formatting, workspace/all-targets Clippy, generated TypeScript API schema and the
  TypeScript/Vite production build pass. No visual UI behavior changes in this increment.

The first full workspace run passed **334 tests**, failed **one**, and ignored **five**
before stopping. Its failure is the unchanged
`a_stalled_job_does_not_hold_up_the_next_one` two-second deadline at
`crates/queue/tests/import.rs:1027`. The latest transaction adjustment was checked by workspace Clippy and fresh
SQLite/PostgreSQL tests. An intermediate wider run also hit that timing failure and
a doc-build mismatch caused by editing source while the run was still active; that
run is not final-source qualification.

After freezing source, the final workspace run passed **395 tests**, with **zero
failures and six ignored**, including PostgreSQL, all doc builds, and the unchanged
stalled-job case. Both privileged FUSE and all three original-writer checks passed
separately. No test deadline was relaxed and no failure was excluded. The earlier
timing failures remain evidence of a flaky gate; this increment does not fix that
queue behavior. See also the final [cache tool receipt](rust-v2-cache-tools-20261103.md).

## Reproduction and upgrade

Use the pinned toolchain, generated data and disposable databases. Builds used
`CARGO_INCREMENTAL=0`, `CARGO_PROFILE_DEV_DEBUG=0`, `CARGO_PROFILE_TEST_DEBUG=0`,
`TOKIO_WORKER_THREADS=4` and `-j3` to bound host resource use.

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -j3 -- -D warnings
cargo test --workspace --locked -j3
cargo test --workspace --locked --no-fail-fast -j3 -- --test-threads=1
npm --prefix ui run api
npm --prefix ui run build
```

Migrations 0012/0013 are additive on both database engines. **Back up `/config` before
upgrading.** The repair journal intentionally has no cascading item foreign key, so
removal cannot erase an uncertain outcome. Terminal rows expire after 30 days;
nonterminal rows require resolution. Source/Arr changes block dispatch. Mutation
uncertainty older than a day requires manual review, rather than fresh automatic writes.
No configuration/setup-wizard change is required.

Detached observation worker, manual abandon/reissue controls, library-root deletion
authority, resumable full-content sweep and PAR2 execution remain pending. A submitted
search is not evidence of a successful replacement. Library, full prefetch policies,
further cache recovery UI and broader migration remain separate inventory work. This branch
must not be treated as a finished port or production-ready cutover.
