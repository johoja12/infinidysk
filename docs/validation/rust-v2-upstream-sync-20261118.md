# Rust v2 upstream sync — 2026-11-18

## Source

- Fork parent: `16e137f02f1bb6a620c4dd07f5c69f6fe7e12217`.
- Upstream target: `8ae494855c6f51614550089b9dce1b7263231068` (`2.0.0-beta.10`).
- Common ancestor: `b07e7a79e0de802de92a4e93a776196fb58be4ad`.
- Four incoming commits: `3f695900` (nonblocking library checks and preview generations), `1ff92fd8` (Cargo updates), `7156d0c6` (UI updates), and `8ae49485` (upstream release metadata).
- The shared checkout started on clean `main`; the dated include-untracked stash attempt reported no local changes. Existing stashes were retained.
- Merge and validation used an isolated worktree. Concurrent file-details work in the shared checkout was excluded from this sync.

## Fork behavior

| Capability | Merge evidence |
| --- | --- |
| Library pointer safety | Bounded pointer inspection, parent confinement, and strict pointer classification retain their fork implementations byte for byte. Read-only root authority is unchanged. |
| Library scans and conversion | Upstream moves filesystem probes off async workers and makes preview publication/confirmation generation-aware. Fork catalogue serialization, protected pointers, retained keys for extra scan roots, subtitle reader release, and Arr link policy remain. |
| Plex classification | The complete-snapshot deadline/status implementation is unchanged. |
| Smart Prefetch | The executor and the configured minimum-size filter in Current warming are unchanged. |
| Arr repair | The durable replacement journal and identity/revision safeguards are unchanged. |
| Cache, streaming, and migration | No source changes in the native cache, NNTP, stream, health, importer, or database crates. No schema or blob-format changes. |
| UI | The manifest conflicts retain the fork's pinned Playwright dependency and test command, alongside all upstream package and lockfile updates. No design change. |

## Validation

- Pinned Rust 1.99.1: formatting and strict workspace/all-targets Clippy passed.
- PostgreSQL-enabled `cargo test --workspace --locked`: **986 passed, 14 ignored**. A separate disposable PostgreSQL 17 fixture was used.
- The newly introduced ignored FUSE freeze regression also passed in an isolated Linux container with `/dev/fuse` and a private mount namespace. This is local fixture evidence, not production FUSE acceptance.
- Node 26: clean optional-dependency install and production UI build passed.
- All **120** fork browser cases passed across three widths and two themes, using an isolated port.
- Migration tooling, mocked announcement tests, and systemd unit verification passed.
- Cargo dependency policy remains a known baseline failure: `chrono 0.4.45`, `RUSTSEC-2026-0543`. Both the fork parent and merged candidate report the same unmaintained error and advisory set. No policy exception or dependency suppression was added.
- npm audit reports the same **13** findings as the fork parent: 2 moderate and 11 high, with identical advisory identities. No forced dependency rewrite was performed.

This is source integration and local validation. Production deployment, live provider/NAS acceptance, and remote CI remain separate evidence.
