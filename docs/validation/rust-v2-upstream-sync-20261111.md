# Rust v2 upstream sync — 2026-11-11

## Sources

Fork main and refreshed origin/main started at
`d6f80c081bc281da6c1e4665027227f663ae7509`. The working tree was clean;
the requested dated stash command returned `No local changes to save`.
Existing stashes were retained.

Upstream main is Rust edition 2024, with a Cargo workspace, native FUSE,
shared VFS, embedded React UI and the pinned Rust 1.99.1 toolchain.
The common ancestor is `a0984b29399920bbf5e82beabf7fd59d835a8fb5`.
The nine incoming commits are:

- `3a192c56`: README overview screenshots.
- `0bd13e84`: 25 provider profiles.
- `b77a82f7`: distinguish transient connect failures from server connection limits.
- `6c0f5727`: accurate empty-folder cleanup counts and cancellation.
- `5ac1ab2b`: validate offsets after complete article part headers.
- `159efcbb`: retry failed read-ahead when playback reaches the segment.
- `ba9fb1a2`: retain full outage intervals in statistics windows.
- `78ed0b7c`: log mid-stream WebDAV failures with a reason.
- `c1f2b40d`: avoid extra buffering between segment and response chunks.

## Fork feature inventory and merge mapping

The original 24-row inventory in [fork migration](../fork-v2-migration.md)
and the net-fork review in [parity audit](../rust-v2-fork-parity-audit-20261107.md)
provide the v1 source/history references. Compare both branches against the
common ancestor, rather than treating upstream-only v1 features as fork work.
The maintained fork already contains the Rust ports. This sync keeps them:

| Capability rows | Rust implementation and compatibility evidence retained |
| --- | --- |
| R01–R02: exact original-download repair | `arr/src/repair.rs`, `arr/src/repair/tracked.rs`, provenance/regrab tests. |
| R03–R04: reader-safe, urgent repair | VFS session/revision guards, health admission, app repair worker and tests. |
| R05: resumable fair content sweep | App `content_sweep.rs`, health sweep and DB cursor/repair state. |
| R06: guarded regrabs | App `regrabs.rs`, durable DB repair intents and library authority. |
| R07: bounded PAR2 recovery | Health PAR2 discovery/articles, app repair worker, PAR2 reader/verify and tests. |
| C01–C04: shared final-media cache and original-format reuse | `native-cache` legacy reader/writer/eviction, stream disk/lineage/cache and VFS native/revision modules. Original payload compatibility, ownership, pins and reader guards stay in place. |
| C05: cache administration | Original cache browser/settings/eviction UI, app native-cache API and native-cache diagnostics remain unchanged by incoming changes. |
| L01–L04: library, Plex and playback attribution | App library catalogue/metadata/playback, Plex crate and VFS sessions remain present. |
| M01–M04: fork state and migration operations | Import fork exporter, app upgrade/fork and migration/proof, durable submissions, operational scripts and tests remain present. Existing migrations retain their contents; incoming metrics migrations are additive. |
| S01: completed-link removal persists | Fork DB tombstones and WebDAV/FUSE/VFS behavior remain. Preserve the fork WebDAV deletion regression alongside new upstream streaming tests. |
| S02: cancellation and multipart latency | Ordered NNTP completion, workload/download permits, stream cancellation and deterministic BODY gates remain. |
| S03: media readiness and archive order | Queue/archive source checks and stored-member regression coverage remain unchanged. |
| O01 and observability | Fork source/image identity and metrics/session/cache attribution remain. Upstream outage tracking extends the existing metrics store. |
| Smart Prefetch | `prefetch` crate and app warming executor retain strict reads, shared cache and distinct workload accounting. Upstream demand retry uses the fork's `None` foreground workload, and cannot turn a strict warming failure into playback. |

No legacy application architecture is retained to resolve these conflicts.
Previously documented production/NAS/performance acceptance limits and open
parity follow-ups remain separate from this upstream sync.

## Conflict decisions

- Keep both independent additions in NNTP client and WebDAV tests.
- Move the fork's cloned `Behavior` (including deterministic BODY gates) into
  upstream's reusable loopback acceptor; retain pause/resume and per-article stalls.
- Adapt upstream's new foreground retry argument to the fork's typed workload API.
  Add a regression that proves failed warming read-ahead is not retried as playback.
- Keep upstream cleanup and outage changes alongside fork library protections,
  persistent pointers, reader accounting and cache behavior.

Back up the configured data directory and matching metrics/repair/cache catalogues
before upgrading. No deployment or production data mutation is part of this sync.

## Validation

- `cargo fmt --all --check`: passed.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: passed.
- `cargo deny check`: advisories, bans, licenses and sources passed.
- Focused playback retry and strict warming regressions: two passed.
- UI TypeScript/Vite build: passed; existing large-chunk warning remains.
- Generated OpenAPI types: match after normalizing a generated UUID default.
- Generated browser fixtures: 84 passed across 360, 768 and 1280 pixel layouts,
  in both themes. These are local fixtures, not live Plex/Arr acceptance.
- Migration operations tests: 27 passed. All four systemd templates and the
  mocked announcement-script checks passed.

Rust uses the repository-pinned 1.99.1 toolchain. UI validation uses the available
Node 24.21.0 runtime. `npm ci --include=optional` uses an isolated temporary npm
cache because the host's default cache contains root-owned files. Package pins
and lockfiles were not changed to repair the host.

The full Rust suite uses a disposable PostgreSQL 17 server. Disk pressure during
compilation required a separate target directory, disabled incremental caching
and disabled debug symbols through environment variables. Debug assertions stay
enabled. Only new compiler cache files from this run were removed from the
earlier shared target. No source or version pin was changed for disk recovery.

The initial warming regression incorrectly assumed one physical attempt. The
existing NNTP pool permits two connection retries (`MAX_RETRIES` in `pool.rs`).
The test instead proves the warming call reports failure and all its attempts
retain warming accounting, with no playback workload. This checks the required
behavior without changing the existing retry policy.

The first PostgreSQL run used a passwordless trust-auth URL. The existing
upgrade/import fixtures parse a `user:password` URL and failed at that parsing
step. With an explicit disposable fixture password, the focused upgrade test
and all five PostgreSQL importer tests passed. No production credential was used
and no assertion or application code was changed for this setup correction.

Final `cargo test --workspace --locked --no-fail-fast`: 909 passed, zero failures, 12 ignored. PostgreSQL cases ran against the disposable server. Ignored cases include kernel FUSE, original C# writer/NAS/local-input qualifications and subprocess/export helpers. Remote CI, native release packaging on both architectures, live providers and production acceptance remain unverified.
