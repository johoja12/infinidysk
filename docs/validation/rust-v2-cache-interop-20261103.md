# Rust fork cache compatibility checkpoint — 2026-11-03

Source specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
Rust baseline: upstream `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
This is local validation of a draft branch. No production cutover or live provider,
Plex, Stash, media-decoder or performance qualification is implied.

## Results

- Final workspace run: **337 passed, zero failed, six ignored**, including a real
  PostgreSQL 17 fixture. SQLite/PostgreSQL share the new durable scheduling checks.
  Build parallelism was four jobs, test parallelism one, and debug symbols were off.
- The three ignored C# interoperability checks passed separately. The harness compiles
  unmodified 1.x storage, filesystem, identity, repair-ledger and MemoryPack source.
  Full import and shared VFS playback/seeks pass with no providers configured.
- Both ignored privileged kernel FUSE tests passed separately. In addition, a file
  copied from the actual C# cache passed full SHA-256 and seek checks through the
  canary's real kernel mount. The local-sample NZB test remains unrun.
- TypeScript/Vite build, Rust formatting and workspace/all-targets Clippy passed.
- Chromium: ten existing native-cache checks passed, then six checks for the legacy
  import UI, file hash, seeks, pin preservation, persisted progress and mobile width.
  No browser page errors were observed.
- HTTP fault check: temporarily removing a synthetic legacy payload paused import
  without advancing the cursor or counting the block as corrupt. Restoring it resumed
  successfully. Repeating the batch was safe and the resulting full-file hash matched.

The first full attempt hit an existing queue timing assertion under concurrent work.
A later attempt crashed in the linker while the host disk was nearly full. Removing
only this task's generated debug artifacts and reducing build/test concurrency produced
the complete passing run above. Those failed attempts are not counted as passes.

## Compatibility and operational boundaries

This is an explicit verified **copy import** into separate Rust storage, not in-place
adoption or shared writers. It requires the original offline catalogue/repair ledger,
registered volume identities and new import provenance. It preserves the 1.x payloads
for rollback and consumes destination capacity. Unproven source generations, missing
provenance and corrupt blocks are not guessed from names or lengths. Storage failures
pause instead of advancing past the current block. See the migration inventory and
fixture README for setup and reproducible commands.

Reader-held repair state survives restart and wakes after the last overlapping reader
closes. Complete native copies are verified before destructive source recovery;
storage uncertainty postpones replacement. Confirmed container defects still repair.
Read-triggered urgency persists through inconclusive provider checks with bounded retry.

The full fork migration is still incomplete. Plex/Smart Prefetch, library catalogue
parity, broader fork data/operational migration, cache recovery/telemetry tools and
PAR2 executor integration remain outside this validated checkpoint.

New main-database migrations are additive. **Back up `/config` before upgrading.**
