# Rust 2.x upstream sync — 2026-11-05

## Source identity

The existing `feat/rust-v2-fork-migration` branch at
`43be91f462098c63e87e19894a6ae86f20a34e0b` incorporates upstream `main` through
`d4f5daf5991447d2726a5b22d9232c5d9c265c94`. There are eight upstream commits
since the shared ancestor `27ce0448d4b6a27f4ffe990dadf2b195c29d72e5`.

This brings in upstream OIDC sign-in, nightly database backups and restore,
SQLite-to-PostgreSQL migration, workload throughput bars, Arr reconnect and
import-message fixes, the memory overview correction, and beta.2 release metadata.

## Fork integration

- Preserve fork repair, library, Plex, Smart Prefetch, native cache and legacy
  migration modules alongside upstream initialization and services.
- Include warming in the new workload byte-rate chart and metrics tally, without
  counting warming attempts twice.
- Regenerate API types from the combined server and retain the fork Settings
  panels alongside upstream database migration controls.
- Extend upstream database transfer from 16 tables to all 38 application tables.
  The additional tables hold repair checkpoints and holds, library indexes,
  warming budget/history, scan journals and migration evidence. Copy, verification
  and live change capture cover all 38, including parent/child ordering.
- Treat only exact untouched migration singleton defaults as an empty target.
  Existing sweep epochs, repair epochs and reserved/charged warming budgets
  prevent replacing a used target.
- Preserve deterministic stalled-import coverage and adapt fork examples and
  legacy settings translation to upstream database-source and OIDC validation.

Back up `/config` before upgrading; existing additive migrations apply on startup.
Upstream database backups and transfers cover the application database. External
native cache payloads, cache catalogues, repair ledgers and metrics remain separate
state and need their own matching backups. Do not share writable cache directories
between the 1.x and Rust processes.

## Validation

Validation uses local isolated SQLite and PostgreSQL 17, with no production
cutover or deployment. The upstream sync does not repeat the earlier genuine 1.x
or privileged kernel FUSE qualification; those remain evidence for their recorded
source revisions in the linked migration receipts.

- `cargo fmt --all --check`: passed.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: passed.
- `cargo test --workspace --locked`: 712 passed, zero failures, six ignored.
  PostgreSQL tests used an isolated PostgreSQL 17 fixture. All eight transfer
  tests passed both in the focused run and the full suite.
- The six ignored cases require separate kernel FUSE, real C# writer or local
  NZB fixtures. FUSE warnings in unprivileged application integration tests do
  not qualify privileged kernel behavior.
- Python operational tooling: 27 tests passed.
- API generation and TypeScript/Vite UI build: passed. Vite reports its standard
  large-chunk size warning.
- All four operational systemd templates: syntax validation passed.
- Conflict marker and whitespace checks: passed.

No production provider, playback performance, deployment or migration cutover
acceptance is claimed by these local results.
