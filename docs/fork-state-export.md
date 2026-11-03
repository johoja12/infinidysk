# Preserve fork state before a Rust migration

The 2.x importer does not yet translate every fork-only setting, repair journal or
external import-runner checkpoint. Preserve them before changing the installation.
Keep the complete original `/config`, blobs and cache volumes for rollback. The
commands below create an additional private evidence package; it is not a full
installation backup and does not activate any setting or job.

This command supports **SQLite source installations only**. A configured non-SQLite
`DATABASE_PROVIDER` is rejected before exporting; a leftover `db.sqlite` does not
establish that it is authoritative. Run with the actual legacy environment. The
PostgreSQL-source adapter remains pending; use its database-native backup and keep
all fork configuration/journals until that adapter is qualified. Rust destination
PostgreSQL support does not imply legacy PostgreSQL-source migration support.

Stop the 1.x application and its migration/import runners. Use a new package directory
on local storage with enough free space for SQLite snapshots. Do not point the export
at a payload volume or an existing directory.

```sh
infinidysk fork-export /legacy-config /local/fork-state-package \
  /operations/import-runner-journal.jsonl /operations/mapped-export-receipt.json
```

Extra journal arguments are optional, explicit absolute files. Include your runner's
actual resume journal and package receipt when they live outside `/config`. The tool
never searches unrelated directories, changes runner state or acknowledges imports.
Source-relative references inside an attached journal remain unchanged.

Record the digest printed by export separately. Verify a transferred package against
that original receipt:

```sh
infinidysk fork-verify /local/fork-state-package EXPECTED_DIGEST
```

A different digest, modified payload, unsafe package filename or missing manifest
fails verification. This is content integrity bound to the recorded receipt, not
signed publisher authentication. Export refuses an existing destination; it does
not overwrite a previous package. A failed export removes only its newly created
incomplete directory. The manifest is written and synced last.

## Preserved data

- `db.sqlite`: a consistent SQLite snapshot, including committed WAL data and unknown
  fork tables/settings. Legacy mutation intent/outcome records remain evidence.
- `metrics.sqlite`, when present, preserves provider quota and operational history.
- The configured native metadata directory's `catalogue.db`, `prefetch.db` and
  `repair-revisions.db`, when present. The directory defaults to
  `/legacy-config/native-cache-metadata`; an effective `cache.native.metadata-path`
  override selects its original location.
- Known config journals: `regrab/library-link-removals.jsonl`,
  `prefetch-predictions.json`, `plex-library-metadata.json` and
  `imported-content-sweep.json`, when present.
- Every explicit extra journal. SQLite files are recognized by their header and
  snapshotted rather than copied without their WAL.
- All effective legacy settings, including unknown fork keys and
  `NZBDAV_CONFIG__...` overrides, plus the six legacy fallback environment variables
  that were actually supplied. This private record includes credentials; values are
  never printed by these commands. Run export with the intended legacy environment.

The output directory is mode 0700 and package files are mode 0600. Package-local
filenames are numeric; original source paths stay in the private manifest. A package
has at most 64 entries, each database at most 4 GiB logically/on disk and each other
record at most 256 MiB. The manifest is capped at 128 KiB. Bounds are rejection points,
not truncation. SQLite snapshots are individually consistent. Source file/WAL identity,
size, modification/change stamps and streamed content hashes are compared before and
after the whole pass. This also catches same-length writes within a timestamp tick;
it does not replace stopping writers for cross-database consistency. Hash checks add
source I/O proportional to database/journal sizes, without loading files into memory.

## Using preserved state

Keep the original package immutable. It is read-only evidence for migration adapters
and operator reconciliation. Uncertain Arr writes must be observed against the exact
configured instance and source identity before any new mutation. A legacy job ID or
path does not prove a Rust source revision. Do not replay old commands or copy the
archived settings directly over Rust settings.

The existing verified 1.x native-cache copy importer remains the payload migration
path. This export does not copy cache payloads/NZB blobs, reconstruct lost cache data,
rewrite links, resume runner submissions or claim that the remaining fork workflows
are already ported. Source caches and their original revision ledgers must stay intact
for cache-copy validation and rollback.
