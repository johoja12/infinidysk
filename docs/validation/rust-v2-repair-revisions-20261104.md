# Durable repair revisions and shared-cache integration

Rust source under test: `28d3f0f5`, following durable ledger `8628b71d` and PAR2
checkpoint `1cbf1831`. This document changes no Rust source. Fork 1.x remains
read-only at `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

## Implemented

- Local SQLite article repair ledger with atomic batches, source proofs, complete
  geometry, payload hashes and content revisions independent of payload retention.
- Fsynced identity/epoch anchor, exclusive writer ownership, bounded records/payloads,
  guarded filesystem access and explicit readback after uncertain publication.
- Actual ledger implementation of the NNTP verified-article source. BODY/STAT bypass
  provider missing/circuit state for verified repairs and fail on unavailable repair
  payloads. Cache hits still check repaired geometry.
- Admission-time revision tags in normal and abandoned response sinks. Decoded memory,
  disk and in-flight cache identities exclude older original/repair generations.
- Final-media keys include relevant article repair revisions. Original unmodified
  sources retain identical keys. Related read handles reopen after repair; unrelated
  handles continue. One fingerprint is captured for both native lease and read guard.
- Analysis/warming guards and removal of hole suppression after verified repair.
- Original C# cache full-import test now attaches an empty Rust repair ledger, proving
  unchanged C# generations still serve provider-free VFS reads and seeks.

The ledger is a storage primitive: its caller must supply whole-file-verified patches.
It does not discover source identity or authorize repair by itself. Runtime bootstrap,
external cache lineage witnesses and the automatic PAR2 worker are still open.

## Validation

- Workspace formatting: passed.
- Workspace all-target warnings-denied Clippy: passed.
- Full workspace with SQLite and task-owned PostgreSQL 17: **493 passed, 0 failed,
  6 ignored**.
- Focused NNTP/cache/VFS run: **121 passed**, with one original-writer test ignored.
- Three original unmodified C# writer interoperability tests: **passed separately**.
  This includes the updated full import with the Rust repair layer attached.
- Two actual privileged kernel FUSE tests: **passed separately**.

The new tests cover atomic batch failure, post-commit anchor failure, readback without
replay, rollback/lost metadata, corrupt payload/geometry, writer ownership, directory
replacement, symlinked anchors, payload eviction/restart and shared catalogue use.
Stream tests hold old responses while repaired reads complete, check cancelled-body
stamps, reopen versioned disk keys, reject wrong geometry and keep base namespaces
separate. VFS tests cover dependent handles, unrelated keys/readers, recorded holes,
ledger restart and final-media data surviving repair-payload eviction.

The optional local NZB corpus is unavailable. The five privileged/original-writer
ignored cases were qualified separately above. No live provider, Plex or media-player
acceptance, deployment or performance percentage is claimed. The application has
not yet activated the repair source at bootstrap. Draft PR #235 remains incomplete.

The local repair tables are additive. Back up `/config`, including cache metadata
and repair anchors, before upgrading. No user setting or setup-wizard control is
introduced by these storage/stream APIs.
