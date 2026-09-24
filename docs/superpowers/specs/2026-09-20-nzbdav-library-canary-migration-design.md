# NzbDav Library Canary Migration Design

## Goal

Prove that legacy NzbDav releases can be reconstructed, imported, correlated, and streamed through InfiniDysk without modifying the production library or registering a new Plex library. Phase one creates a local nuc-1 validation tree at `/mnt/plex2` containing 20–50 representative media symlinks.

## 2026-09-24 extension: second legacy link root

Legacy links also exist under `/mnt/special`. The parallel validation tree for
that source is `/mnt/special2`. Preserve both existing roots and their relative
paths; sample 20–50 links from each root in separate canary packages. The
current migration package and host tools
represent one library root, so inventory, export, migration session, link plan,
apply journal, rollback, benchmark, and coverage must be separate for each pair.
The NZB blob catalogue may be shared. Check for duplicate legacy DavItem IDs
and targets across the two inventories before export; target reuse needs a
reviewed procedure before a duplicate enters a second import. Combined coverage
uses the sum of covered links divided by the sum of final links across both
roots, with per-root reports retained. See the updated
[operator guide](../../guides/nzbdav-migration.md) for the commands and gates.

## Why this approach

The production library contains 41,274 symlinks targeting legacy `/.ids/...` DavItem IDs. Only 153 linked IDs descend from retained completed-history rows; the legacy blob store contains 55,104 additional XML-like NZB files. Replaying retained history alone is insufficient, replay creates new IDs, and filename matching is not a safe identity mechanism.

The least-disruptive design therefore combines:

1. a read-only legacy export,
2. the existing resumable InfiniDysk migration framework,
3. normal InfiniDysk queue imports,
4. article-identity correlation, and
5. a new local-only symlink tree.

Direct database transplantation and production symlink rewriting are explicitly out of scope.

## Safety boundary

- Existing NzbDav remains authoritative and online.
- `/mnt/plex`, `/mnt/remote/nzbdav`, Plex, Arr, and existing rclone services are read-only inputs for discovery and are never modified or restarted.
- `/mnt/plex2` is local to nuc-1 and is not added to Plex during phase one.
- The legacy PostgreSQL database and blob store are accessed read-only.
- InfiniDysk imports use dedicated migration-only categories that no Arr instance monitors.
- Segment-cache data is not copied between products.
- No unmatched or ambiguous record is guessed into a mapping.

## Canary scope

Select approximately 12–20 releases producing 20–50 media leaves. The exact list is a reviewed artifact before export or import. It should include:

- TV, movie, anime, HD, and 4K paths from the real `/mnt/plex` hierarchy;
- MKV and MP4 leaves;
- direct NZB, RAR, and multipart representations;
- small, medium, and large files;
- releases with multiple leaves where available;
- known-good warm reads and cold reads that have not been pre-warmed.

Known-broken, missing-article, quarantined, or currently repairing items are excluded from the success canary and listed separately as negative-test candidates.

Six of the successful leaves form a reviewed performance subset. Reuse the same
library-relative path on `/mnt/plex` and `/mnt/plex2` so every measurement compares
the legacy and InfiniDysk representations of the same media. The subset must cover
direct NZB and RAR/multipart media, HD and 4K, and at least one large file. Record
the exact six paths in a benchmark-selection artifact rather than selecting files
randomly at run time.

## Architecture

### 1. Read-only legacy exporter

Add a bounded export tool that runs against explicit legacy inputs and emits an immutable canary package. It must not connect with write-capable database credentials or mutate the blob store.

For every selected library link, record:

- library-relative path;
- original absolute target and parsed legacy DavItem ID;
- legacy DavItem path, subtype, size, parent/release relationship, and history ID when present;
- source NZB blob identity;
- ordered article/message IDs and a deterministic identity digest;
- extraction status and any ambiguity or missing-data reason.

The package contains only the NZB XML needed for selected releases plus a versioned manifest. It excludes application credentials, provider configuration, database dumps, cache bytes, and unrelated NZBs. Write the package atomically and generate a checksum inventory.

### 2. NzbDav migration source adapter

Extend the existing source-neutral migration engine with `MigrationSourceTypes.NzbDav` and an NzbDav package reader. Reuse the existing session state machine, bounded submission workers, queue-depth guard, claim recovery, reconciler, provenance, and history handling.

The adapter scans the package rather than the live legacy installation. Each release is assigned a dedicated target category and submitted through the normal `addfile`/queue path. Default phase-one execution uses one worker and a low maximum queue depth.

### 3. Strong correlation and provenance

Extend source-file provenance to store a source-stable identifier, specifically the legacy DavItem ID. Match imported InfiniDysk leaves using ordered article identity plus exact size and release membership. Filename and normalized relative path may break ties only after the strong identity fields agree.

Classify every candidate as one of:

- `exact`: one source leaf maps to one imported leaf;
- `duplicate`: multiple equivalent source candidates require explicit selection;
- `ambiguous`: evidence permits multiple non-equivalent mappings;
- `missing-source`: the selected leaf cannot be reconstructed from available NZBs;
- `import-failed`: InfiniDysk did not complete the normal import;
- `unmatched-target`: import completed but no target leaf meets the identity contract.

Only `exact` mappings can create `/mnt/plex2` entries.

### 4. Local parallel-library builder

After review, create `/mnt/plex2` on nuc-1 and reproduce only the selected relative paths from `/mnt/plex`. Every new symlink points to the canonical InfiniDysk target returned by `DatabaseStoreSymlinkFile.GetTargetPath`, rooted at `/mnt/remote/infinidysk`.

Before creation, write a manifest containing the intended link path, legacy target, new target, mapping evidence, and source package checksum. Apply must:

- reject paths outside `/mnt/plex2`;
- reject symlinked parent directories;
- refuse to overwrite any existing file, directory, or differently targeted symlink;
- revalidate the mapping and target immediately before link creation;
- support idempotent reruns when an existing link already has the intended target.

Because `/mnt/plex2` is new, rollback removes only links created by the recorded canary run and then prunes empty directories. It never follows or deletes link targets.

## Data flow

```text
/mnt/plex selected links + legacy PostgreSQL + legacy NZB blobs (read-only)
        │
        ▼
versioned canary export package + checksums
        │
        ▼
NzbDav source adapter → existing migration queue runner → InfiniDysk import
        │
        ▼
article identity + exact size correlation → reviewed exact mappings
        │
        ▼
/mnt/plex2/<original relative path> → /mnt/remote/infinidysk/.ids/<new-id>
```

## Validation gates

### Before import

- Review and approve the exact canary list.
- Confirm all legacy reads are bounded and read-only.
- Verify the package checksum inventory and ensure no secret-like values are present.
- Confirm migration categories are not monitored by Arr.

### Per release

- Normal InfiniDysk import reaches completed history.
- Expected and imported leaf counts reconcile.
- Every linkable leaf has an `exact` identity mapping.
- File size matches exactly.

### Per `/mnt/plex2` leaf

- Link target exists on the InfiniDysk mount.
- `stat`, WebDAV HEAD, and beginning/middle/end range reads succeed.
- A bounded `ffprobe` succeeds for media formats it supports.
- The six-file performance subset is tested side-by-side through
  `/mnt/plex/<relative-path>` and `/mnt/plex2/<relative-path>`.
- Each side records three bounded seeks at approximately 10%, 50%, and 90% of
  the file and a bounded sequential read of `min(128 MiB, file size)`. Record time
  to first byte,
  seek/read completion latency, sustained MiB/s, bytes read, timeout/error, exact
  file identity and size, source mount, WebDAV route/port, and timestamp.
- Run a first-pass and repeat-pass matrix. Label a result `observed-cold` or
  `observed-warm` only when cache inspection proves that state; otherwise use
  `cache-state-unknown-first-pass` or `cache-state-unknown-repeat-pass`. Do not
  drop host caches or purge either rclone cache for the canary.
- Store raw machine-readable results and a human-readable table containing the
  actual legacy and InfiniDysk numbers for all six files. Results obtained while
  InfiniDysk rclone still uses frontend port 3004 are diagnostic and cannot be
  called backend-throughput acceptance evidence.
- Manual playback is recorded separately from automated acceptance.

### Phase-one acceptance

- 20–50 symlinks created with zero unexplained mapping differences.
- No writes under `/mnt/plex` and no Plex/Arr configuration changes.
- Existing NzbDav health, mount readability, and container restart counts remain unchanged.
- Every failure or exclusion is accounted for in the canary report.
- The canary report contains the six-file throughput and seek comparison with no
  omitted, timed-out, or relabelled measurements. Functional canary success
  requires every bounded read and seek to complete; performance conclusions are
  deferred when route or cache state is not comparable.

## Rollback

Pause the migration runner, retain the immutable export and reports, remove only `/mnt/plex2` links recorded by the selected canary run, and prune only newly empty `/mnt/plex2` directories. Imported InfiniDysk content remains available for diagnosis until separately approved for deletion. Legacy NzbDav and `/mnt/plex` require no rollback because they were never changed.

## Deferred production migration

Phase one does not solve final production cutover. After canary acceptance, a later design must decide between a complete legacy-ID compatibility resolver and a fully verified production symlink rewrite. No Plex library registration, distributed rclone cutover, or mass import is authorized by this design.
