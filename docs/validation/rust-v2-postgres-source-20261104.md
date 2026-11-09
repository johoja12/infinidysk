# Legacy PostgreSQL source snapshots

The Rust importer can consume a private PostgreSQL snapshot through its existing
SQLite adapter. `fork-pg-snapshot` opens one read-only repeatable-read transaction,
retains every public base table and its PostgreSQL column types, and streams all
original textual rows into separate evidence. The adapter preserves UUID strings,
booleans, signed integers, binary values and nulls. Unknown fields/tables remain in
both the adapter and raw evidence; they are not silently activated as Rust jobs.

The source container's IANA timezone is required. Wall-clock timestamp columns keep
their original local values, and import/retry conversion uses that recorded timezone
instead of the new host timezone. Npgsql infinity sentinels retain their original
raw evidence and become .NET min/max dates at importer millisecond precision.
Timestamp-with-timezone columns require explicit translation review. Non-public
base tables are refused. This is migration evidence, not a PostgreSQL restore backup:
retain a native PostgreSQL backup, source configuration and the 1.x rollback image.

The directory is owner-only, files are owner-only and all fixed local paths are
SHA256-bound with the timezone in a receipt. Connections come from bounded stdin;
connection settings are not recorded or printed. Driver errors at the source boundary
are redacted. The exporter never updates PostgreSQL. A pending marker fences an
interrupted directory from both verification and legacy auto-import. Failure cleanup
removes only the new directory owned by that attempt. Existing destinations are refused.

The source installation must be stopped before taking its final database snapshot
and copying its configuration blobs, metrics and operational journals. A database
transaction alone cannot make external files consistent. Use a separate copied config
and Rust database/cache/mount/library roots; never run this against writable shared
production storage. Payload caches still use the separately verified cache-copy path.

```sh
# The private input contains a PostgreSQL URI, never put it in command arguments.
infinidysk fork-pg-snapshot /private/new-config America/New_York < /private/source-connection
infinidysk fork-pg-verify /private/new-config <retained-export-digest>
```

Copy the stopped source's `blobs/`, optional `metrics.sqlite` and journals into the
new config without replacing the snapshot files. Retain environment-owned settings
separately. Normal `import-report` and legacy import use this adapter and verify the
snapshot before opening it. Large checksum validation runs off the async executor.
`fork-export` also preserves the PostgreSQL receipt, column catalog and raw row
archive along with its normal configuration/journal evidence.

## Validation scope

600 locked workspace tests passed, zero failed and six default ignores, with actual
SQLite and PostgreSQL 17. Formatting and all-target warnings-denied Clippy passed.

Generated PostgreSQL 17 tests exercise unknown journal/settings rows, UUID, boolean,
binary/null/large-integer values, fractional local timestamps and infinity sentinels.
An actual survey and apply into a new Rust SQLite database prove that a generated
folder's local creation time becomes the correct UTC instant in the source timezone.
The source remains unchanged. Tests also cover nested evidence export, existing-path
refusal, unsupported-type cleanup, interrupted exports and tamper rejection.

No production database snapshot, live Arr/Plex acceptance, native disaster recovery,
image publication, deployment or cutover is claimed. Operational mapped imports and
legacy job activation remain separate work. No runtime Settings/wizard control changes.
