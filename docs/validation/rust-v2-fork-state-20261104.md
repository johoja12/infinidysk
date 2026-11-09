# Rust fork-state preservation checkpoint — 2026-11-04

This increment adds `fork-export` and `fork-verify`, using the Rust importer and CLI.
Specification: fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.
All test data is generated. No production source, provider or old migration runner is
read or changed.

## Behavior and limits

The export rejects a configured non-SQLite legacy provider, including a PostgreSQL
installation with a leftover SQLite file. PostgreSQL-source translation is pending.
The export snapshots legacy SQLite databases with committed WAL data, preserves
unknown tables/settings, and archives known config journals plus explicit runner
attachments. SQLite recognition for extra files uses the header rather than a file
extension. Effective environment overrides and the six legacy fallback variables are
stored privately alongside the original stored settings in the database snapshot.
No setting, job, repair intent, old acknowledgement or payload cache is activated.

The package directory is private, files are private and created without overwriting,
package filenames are fixed numeric names, and its manifest is synced last. Failures
remove only the newly created incomplete directory. Symlinks/hard-linked files and
unknown package paths are rejected. Export checks source identity, size, timestamps
and streamed content hashes before/after the pass. Each SQLite backup is individually
consistent; writers must be stopped for a consistent cross-database snapshot. This is
additional evidence, not a complete `/config`/blob/cache-volume backup.

Verification checks every payload and the manifest digest. The CLI additionally
requires the expected digest from the original export receipt; changing both a payload
and its in-package hashes cannot satisfy that external receipt. Count/byte limits and
usage are documented in [fork-state export](../fork-state-export.md).

## Validation

The final frozen-source run passes **414 tests, zero failed and six ignored**,
including actual PostgreSQL 17 and SQLite. Workspace/all-target Clippy and formatting
pass. The binary builds with the pinned toolchain and the actual generated-fixture
CLI checks pass:

- Configured PostgreSQL source is rejected before creating a package, even with a
  leftover SQLite database. Unsupported provider names fail as well.
- Committed WAL rows survive the read-only database snapshot. An operational SQLite
  journal with a `.sqlite3` extension is recognized by its header and snapshotted.
- Unknown stored settings, effective environment overrides and supplied fallback
  variables remain in the private package; the source stored values stay unchanged.
- Uncertain repair/runner records retain their exact stage without replay.
- Exported permissions are 0700/0600. Existing destinations are refused.
- Correct external digest verifies. Wrong digest, missing digest argument and modified
  payloads fail without a panic. CLI output does not contain fixture credential values.
- Unit checks also cover symlinks, path traversal, hash byte bounds, same-length source
  changes and corrupt secondary database failure cleaning only the incomplete package.

CLI binary SHA-256:
`5bc4baf199cd179f7da0549d6fda57cac312931e6915dfc4395ee23e45a06645`.
Local receipt: `/tmp/fork-state-cli-validation.json`. CLI sources are generated SQLite
and JSON fixtures, not an actual production C# export. The existing original C# writer
cache interoperability checks are separate evidence: the final-source importer passes
both original-writer tests, and the native-cache legacy suite passes its original-writer
case separately. All three pass sequentially against the immutable generated C# fixture.
Both privileged FUSE tests passed at the preceding classification checkpoint; no FUSE
source changes in this CLI increment.

Earlier full runs again hit the unchanged two-second stalled-queue timing case; its
isolated run passes. The final 414-test green run does not establish that this timing
flake is fixed. The local-sample NZB ignored test still needs an external sample.

The first compilation caught unsupported direct SHA-256 hex formatting. Initial lint
checks caught a stack buffer and empty-string construction. Both were corrected.
A meaningful source-change test then demonstrated that same-length writes may share
filesystem timestamps. Export now compares content hashes as well; the test was not
weakened with a sleep or a timestamp assumption.

This feature preserves data for later adapters. It does not claim typed activation of
all fork settings, legacy journal interpretation, production cache acceptance,
operational-runner resume parity, live provider qualification, deployment or cutover.
