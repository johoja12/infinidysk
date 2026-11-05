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

The native POCs used source `dc300684c00d7f8701fbbfd32c79d70e710eb1de` and
frozen executable SHA256
`ac3ccf7a3e402eb0c13c71d6cfdabdeba6168641eb4fc651c167ba607988817d`.

- Migration: actual API/queue/NNTP/FUSE execution passed, with an exact new-ID member,
  resumable canary admission and no duplicate dispatch. The original link inode,
  ctime and target and offline database hash remained unchanged. A full cold read
  persisted coverage `[0, 8388608)`. Another fresh native process read all 8,388,608
  bytes and three seek ranges with all providers removed; hashes matched.
- Retirement: actual FUSE reader protection, disabled automatic repair, enabled
  last-close wakeup and source retirement passed. The audit remained present,
  the unrelated generated source hash matched, and the loopback Arr received zero
  writes. This is local retirement of imported state, not deletion from a live 1.x server.
- Chromium: the real legacy status endpoint and Health view passed desktop and
  390px mobile checks, with no page errors or horizontal overflow.
- All three genuine 1.x C# writer/import/cache interoperability cases passed
  sequentially against the original writer fixture. Bytes are copied into separate
  Rust storage; the two runtimes do not share a writable cache root.
- Both privileged kernel FUSE cases passed separately with exit code zero, including
  native-cache reads, cross-UID completed-pointer removal and mount challenges.
  The earlier combined wrapper terminated after reporting both cases passed; only
  the separate clean-exit runs qualify these results.

The release benchmark used seed 1025 and the existing impaired loopback provider.
It did not read production files. Both cold cases verified all 268,959,744 bytes;
all four throughput cases reported matching hashes and byte counts.

| Generated scenario | Observed result |
| --- | --- |
| Cold ramp, default window | 76.795 MB/s; 481.8 ms to 8 MB |
| Cold ramp, prewarmed connections | 76.768 MB/s; 479.7 ms to 8 MB |
| Open-ended 8 MB seeks | First byte median 277 ms, maximum 345 ms; full read median 356 ms |
| Bounded 8 MB seeks | First byte median 109 ms, maximum 168 ms; full read median 178 ms |

Both cold rates exceed the benchmark's historical reference floors (36.263 and
60.231 MB/s). The bounded/open-ended comparison tests existing harness modes; it
is not a before/after measurement of this port. The host was shared and a release
image compilation was active. These measurements are not production speed estimates
or a fresh C# comparison. The very short unpaced throughput cases are retained in
the local reports as hash/byte verification rather than a production capacity claim.

The canonical Dockerfile built a local Linux amd64 image from the same source
commit, including the pinned rapidyenc submodule. Image ID:
`sha256:832208268b264c7c517d2532edbc6c85b05962e1db5fb6f825c6252943c5c8a4`.
The archived source SHA256 was
`0069793c64070800eaa39e76a8ae00f0f8683ed01246f9ea3e21d1fd2e47003b`.

The isolated release container passed its own healthcheck (database, FUSE mount and
queue). With providers absent before startup, a full 8,388,608-byte WebDAV read and
three range seeks matched the generated hash/bytes through the native cache. The
container was stopped and removed. The image was not pushed or deployed.

The POC deliberately kept its mount private to the container and its BDI sysfs
read-only. Startup emitted actionable warnings for those settings and authentication
being disabled on the loopback-only test service. It did not change host propagation,
FUSE policy or readahead. This qualifies local amd64 packaging and cached delivery;
it does not qualify arm64 or production mount/performance settings.

All owned POC processes and mounts were stopped. The temporary PostgreSQL fixture
was removed. The source 1.x checkout and production services remained unchanged.

Generated payloads are
binary fixtures, not playable media. These POCs do not certify live Plex/Arr/provider
behavior, production NAS outages, migration cutover or a production performance gain.
