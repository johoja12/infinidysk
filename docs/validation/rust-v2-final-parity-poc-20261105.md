# Remaining fork parity and native POC — 2026-11-05

## Implementation

Legacy repair observation now has a separate guarded local-source retirement path.
It requires a fresh exact Arr replacement observation, unchanged enabled Arr identity,
original imported content fingerprint and current metadata/provenance. Database
predicates compare the item lifetime, path, size, kind, health state and raw metadata.
Row/writer locks keep concurrent metadata/provenance changes from crossing deletion.
Unresolved bound or global evidence and active Rust regrab intents prevent retirement.

The VFS grants exclusive source admission only after its readers close. Full retained
cache coverage, storage uncertainty and repaired/publishing article revisions retain
the source. Read-only scan-root authority applies. The Health and automatic-repair
switches must both permit the operation. Last-reader close prioritizes the durable
candidate and wakes the observer rather than waiting out its retry interval.

Owned reader, publication and maintenance guards remain with the SQL mutation task
until its transaction settles, including cancellation of the caller. Retirement and
its audit receipt commit atomically. Immutable legacy evidence survives deletion and
restart; it never becomes authority to replay an Arr mutation. Only the local imported
file is retired. The offline 1.x source database, journals and rollback cache are unchanged.

The authenticated Health view lists up to 128 legacy records and distinguishes
unresolved evidence, held retirement, absent sources and retired sources. It exposes
no original private payloads or proof manifests and provides no unsafe force-release.

Migration 0025 adds nullable retirement audit columns to existing evidence tables on
SQLite and PostgreSQL. **Back up `/config` before upgrading**, together with external
cache and repair state. Application database transfer already discovers these columns
for all 38 tables. Setup-wizard review: no new configuration; existing advanced Health,
automatic-repair and library-root policies remain authoritative.

## Streaming and archive verification

- A gated loopback provider holds the first BODY indefinitely. Cancellation must
  release the single download permit after the transport timeout, replace its socket
  and successfully read the next article without opening the gate.
- Invalid MKV signatures inside stored multipart RAR and stored 7z files fail before
  queue handoff. Direct-file and nested/compressed archive checks remain covered.
- A generated RAR set whose filenames sort opposite to volume order imports correctly
  when the last volume is listed first. Existing obfuscated/multiple-set and real
  RAR4/RAR5/7z fixture tests retain their own coverage.

## POC reproduction

Build the UI and `fork_local` example from a clean exact source commit with
`INFINIDYSK_COMMIT` and `INFINIDYSK_SOURCE_REPOSITORY=johoja12/infinidysk` set.
Copy the executable to a separate frozen artifact path before running the scripts.
Both scripts require a disposable root-capable Linux FUSE environment, generate their
own private roots and loopback provider, refuse to overwrite their output receipt,
and stop their owned process on exit:

```
sudo python3 scripts/fixtures/qualify-rust-fork.py --binary /path/to/frozen/fork_local --output /tmp/new-migration-receipt.json
sudo python3 scripts/fixtures/qualify-rust-retirement.py --binary /path/to/frozen/fork_local --output /tmp/new-retirement-receipt.json
```

The migration POC exercises actual Rust API admission, the queue, generated NNTP,
exact new-ID member discovery, process-bound mount challenge, separate canary links,
resume without redispatch, full cold fill, and fresh-process providerless cache reads
and seeks. Original library links and offline source bytes remain unchanged.

The retirement POC uses a real native FUSE reader and loopback Arr HTTP server. It
checks reader protection, disabled automatic repair, last-close wakeup, retirement,
retained audit, zero Arr writes and an unchanged unrelated source hash. Its scheduling
clock is advanced only in the owned fixture to avoid waiting minutes for refusal
checks. The final enabled last-close test does not advance that clock.

## Qualification record

The final local workspace run passed **717 tests**, with zero failures and six
fixture-dependent cases ignored. Formatting and warnings-denied all-target Clippy
passed. API generation and the TypeScript/Vite build passed (standard large-chunk
warning only). All 32 operational Python tests and all four systemd template syntax
checks passed.

Two earlier validation failures were fixed before that final run: the API test
opened a database writer before startup migration completed, and a new wakeup SQL
statement omitted its priority parameter. The corrected startup and last-reader
regressions pass, including the database path on SQLite and PostgreSQL.

Frozen-source POC results will be recorded after execution. Generated payloads are
binary fixtures, not playable media. These POCs do not certify live Plex/Arr/provider
behavior, production NAS outages, migration cutover or a production performance gain.
