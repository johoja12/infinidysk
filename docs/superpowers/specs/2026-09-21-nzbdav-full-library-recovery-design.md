# NzbDav Full-Library Recovery and Parallel-Link Design

**Issue:** [johoja12/infinidysk#13](https://github.com/johoja12/infinidysk/issues/13)

**Goal:** Recover and import at least 90% of the NzbDav-backed links in the live
`/mnt/plex` library into InfiniDysk, then create a parallel, unscanned
`/mnt/plex2` tree with the same relative paths and InfiniDysk `.ids` targets.

## Production evidence

The Phase B canary established the starting point:

- The checked inventory contains 41,306 NzbDav-backed library symlinks.
- Only 166 links remain attached to retained `HistoryItems`; 41,140 are marked
  `missing-history` by the current exporter.
- A SELECT-only diagnostic found all 41,140 legacy `DavItem` rows. Every row has
  an exact size and non-empty type-appropriate article metadata: 18,805 direct
  files, 36 RAR files, and 22,299 multipart files.
- The legacy blob store contains about 55,104 unreferenced files (about 49 GB);
  bounded sampling found XML-like NZB payloads.
- The 30-file canary imported all 16 submissions, but path-dependent archive
  identity correlated only 21 files. All nine unmatched targets exist at their
  expected sizes and are unique within their imported release.

Removing the canary's 50-link selection limit cannot meet the full-library goal.
The migration must recover source NZBs from the orphan blob population before it
submits bulk work.

## Safety boundary

- NzbDav, `/mnt/remote/nzbdav`, `/mnt/plex`, Plex, and Arr remain authoritative
  and unchanged throughout this work.
- `/mnt/plex2` remains a local nuc-1 directory outside Plex and Arr.
- Every `/mnt/plex2` link points beneath `/mnt/remote/infinidysk/.ids`; no link
  may fall back to NzbDav.
- The legacy database is accessed with the existing SELECT-only role inside a
  repeatable-read, read-only transaction.
- The orphan blob tree is read-only input. The catalogue rejects symlinks,
  non-regular files, traversal, unsafe XML, and files that change while read.
- InfiniDysk receives only checksummed, read-only packages. It never receives
  write access to `/mnt/plex`, `/mnt/plex2`, the legacy database, or the legacy
  blob tree.
- No source link is removed or rewritten. Link application is create-only and
  journaled; rollback removes only still-matching links owned by that journal.
- Bulk submission is prohibited until a complete dry run projects at least 90%
  uniquely recoverable coverage.

## Definition of coverage

The initial denominator is a checksummed snapshot of all valid NzbDav `.ids`
symlinks beneath `/mnt/plex` at the start of the full run. Every entry receives
one terminal classification.

Before final acceptance, inventory runs again. A delta pass processes new or
changed NzbDav links created after the initial snapshot. The final denominator is
the fresh final snapshot, and the numerator is the number of its relative paths
that exist as owned, validated InfiniDysk links beneath `/mnt/plex2`.

Success requires:

```text
validated InfiniDysk links / final NzbDav source links >= 0.90
```

Deleted source links are removed from the final denominator but are never
automatically deleted from `/mnt/plex2`; they are reported for separate review.
Every missing link in the final denominator must have an explicit recovery,
import, correlation, or apply classification.

## Architecture

### 1. Immutable source inventory

Extend the host migration tool with a full-library mode that writes an immutable
inventory snapshot containing the library-relative path, original NzbDav target,
legacy `DavItem` ID, type, exact size, article metadata summary, and ancestry
status. The snapshot and its `SHA256SUMS` are create-new artifacts.

The legacy reader must load direct, RAR, and multipart metadata even when no
`HistoryItem` owns the leaf. It must continue proving acyclic ancestry and must
not infer an owner from a filename or path.

### 2. Resumable orphan-NZB catalogue

Add a host command that scans a frozen list of regular files beneath the explicit
legacy blob root into a private SQLite catalogue. The catalogue is separate from
both application databases and records:

- resolved path beneath the approved blob root;
- size and modification snapshot;
- SHA-256 payload digest;
- parse status and sanitized failure class;
- release/article digest material and normalized message IDs;
- duplicate-payload membership.

Parsing uses the same constrained NZB reader as package import: standard NZB 1.1
DOCTYPE declarations are accepted, external resolution is disabled, internal
subsets/entities and other doctypes are rejected, and the file is validated and
parsed through one already-open handle. After parsing, size and metadata are
rechecked so a concurrently changed file cannot enter the catalogue.

The scanner commits bounded transactions and resumes by verified file identity.
A completed catalogue has a checksummed summary with file counts, bytes, valid,
duplicate, corrupt, changed, and missing classifications. Resumption never turns
a prior partial catalogue into a completed one without revalidating its frozen
input list.

### 3. Deterministic source recovery

Recovery joins legacy leaf metadata to the catalogue without using names as an
authoritative key.

- **Direct files:** all normalized legacy segment IDs must resolve, in order, to
  exactly one logical NZB payload and one NZB file. Exact size and the direct
  article identity must agree.
- **RAR and multipart files:** all contributing segment IDs must resolve to one
  logical NZB payload. The candidate must belong to the same recovered release
  and agree on exact published size. Contributing article evidence is retained
  for post-import correlation.
- Byte-identical orphan blobs are one logical payload, not an ambiguity.
- Distinct payloads satisfying the same required evidence are `ambiguous` and
  excluded. Missing segments, empty metadata, invalid XML, changed blobs,
  unsupported item types, and unsafe ancestry receive separate exclusions.
- Paths and names are supporting diagnostics only and can never convert an
  ambiguous or missing match into an exact match.

The recovery report accounts for every inventory row and computes projected
recoverable coverage. Package construction is disabled when that percentage is
below 90%.

### 4. Stable archive correlation

The path-dependent `archive-member-v1` identity remains readable for existing
packages, but new full-library packages add stable archive evidence derived from
the recovered release and the member's contributing article metadata plus exact
size. Published filenames are excluded from the stable identity.

Post-import correlation uses this order:

1. existing direct article identity;
2. stable archive article evidence from new packages;
3. `PathInArchive` only when it reproduces an existing package identity;
4. for the already-imported Phase B package only, same run, same source release
   payload, exact size, and exactly one imported candidate.

The fourth rule is an explicit legacy-package reconciliation method, not a global
filename or size fallback. It refuses multiple same-size leaves, missing run or
release provenance, mismatched payload identity, and non-video competitors.
Every result records its match method and non-secret evidence in the migration
ledger.

### 5. Reconciliation without resubmission

Add an authenticated reconciliation-only operation for a terminal NzbDav run.
It reloads the immutable package, existing migration submissions, migrated
release provenance, and current imported leaves; then updates correlation and
provenance inside a transaction. It never creates a queue item, history item, or
new DAV leaf.

The operation is idempotent. It refuses active submissions, non-terminal runs,
package-digest changes, missing imported-release provenance, and any attempt to
replace an existing exact mapping with a different target.

This operation must reconcile the existing 30-file canary to 30/30 exact before
the full-library run starts.

### 6. Bounded package and import batches

The full recovery report becomes a master manifest. Deterministic batch manifests
partition uniquely recovered logical releases without splitting one release.
Defaults are bounded by both release count and payload bytes; operators may lower
but not bypass those limits. Each batch has its own package digest, payload
checksums, selected links, and stable source IDs.

Staging copies only the current batch's payloads into the read-only migration
input. Payload staging is retained until that batch is terminal, reconciled,
planned, applied, and checksummed in the operator record. Cleanup is a separate
explicit operation and never deletes the legacy blob source.

The existing queue-depth and worker controls remain in force. A batch resumes
from its migration ledger rather than creating duplicate submissions. The next
batch cannot start while the current batch has active submissions, non-exact
selected rows, or an unapplied plan.

Previously migrated legacy IDs, including the Phase B canary, are recognized by
persisted provenance and included in link planning without resubmission.

### 7. Complete plan gating

Canary/full-link plan generation requires all selected rows to be terminal,
`exact`, and actionable. It must reject unmatched, missing-source,
missing-target, duplicate, ambiguous, excluded, or absent rows even when the
ambiguity count is zero.

Each immutable plan contains every selected relative path, the expected size,
the canonical relative InfiniDysk `.ids` target, correlation method, package and
master-manifest digests, and run identity. `ActionableCount`, `SelectedCount`,
and the number of exact rows must be equal.

### 8. Parallel link application

The host applies each verified plan with:

```text
library root: /mnt/plex2
target root:  /mnt/remote/infinidysk
```

For a source path such as:

```text
/mnt/plex/Movies-HD/The Patriot (2000)/The Patriot (2000) WEBRip-1080p.mkv
```

the parallel link keeps the same relative path:

```text
/mnt/plex2/Movies-HD/The Patriot (2000)/The Patriot (2000) WEBRip-1080p.mkv
```

and points to the canonical InfiniDysk target:

```text
/mnt/remote/infinidysk/.ids/e/f/c/3/3/efc33a57-8c90-4772-be04-68ac0bf2946f
```

Immediately before every `symlink(2)`, the applier revalidates plan checksums,
root confinement, absent output, regular-file target, exact target size, and
unchanged source snapshot identity. Existing unowned filesystem objects are
never overwritten. Every successful creation is durably journaled.

An aggregate coverage command combines the immutable source inventories, master
manifest, batch ledgers, plans, and apply journals. It independently walks both
library trees without following symlinks and reports source count, recoverable,
submitted, completed, exact, created, validated, drifted, and every exclusion
class.

### 9. Validation and rollout

The production sequence is deliberately staged:

1. Deploy the correlation/reconciliation and plan-gating fix.
2. Reconcile the existing canary without submissions; require 30/30 exact.
3. Generate, apply, and validate the 30 canary links.
4. Run the full inventory, orphan catalogue, and recovery dry run.
5. Stop if projected recoverable coverage is below 90%.
6. Import deterministic batches with one submit worker and bounded queue depth,
   reconciling and applying each batch before advancing.
7. Refresh the source inventory and run delta recovery/import batches.
8. Require at least 90% owned, size-validated InfiniDysk links against the fresh
   final source snapshot.
9. Run bounded first/repeat seek and throughput tests plus representative
   `ffprobe` checks across direct, eager archive, lazy archive, TV, movie, 4K,
   small, and large files.
10. Leave `/mnt/plex2` outside Plex and Arr. Registration or production cutover
    is a separate explicitly approved change.

At every step, verify InfiniDysk and legacy container health, restart counts,
both rclone mounts, queue state, and that `/mnt/plex` inode/target snapshots did
not change because of migration tooling.

## Failure handling

- Catalogue/recovery failures are terminal classifications, not guessed matches.
- A batch import can pause and resume through existing controls. It does not
  advance while submissions are active or failed without review.
- Reconciliation never downgrades or silently remaps an exact result.
- Plan generation fails closed on any non-exact selected row.
- Link apply stops at the first conflict and retains its durable journal.
- Rollback removes only owned links whose targets still match the journal.
- Coverage below 90%, source drift that cannot be reconciled, or mismatched target
  sizes stops the production run without touching `/mnt/plex` or consumers.

## Data model and compatibility

Additive migration-ledger fields/tables may store catalogue/master-manifest
provenance, batch identity, stable archive evidence, and aggregate link state.
They must be backward-compatible and must not modify the application content
database schema merely to represent host-side catalogue files. Any additive EF
migration requires the release note to tell operators to back up `/config` before
upgrading, but it is not a breaking change.

Existing canary packages and `archive-member-v1` remain readable. AltMount
migration behavior and its planner remain unchanged.

## API and UI

The authenticated NzbDav migration surface adds reconciliation status and action,
batch/master-manifest progress, aggregate exact/created/validated counts, coverage
percentage, and explicit failure classifications. Secrets, raw article IDs, raw
metadata, filesystem credentials, and legacy connection strings never appear in
responses or logs.

This remains an advanced migration workflow rather than new-install critical
configuration. It does not add a `ConfigKeys` setting and does not change the
setup wizard version or completion allowlist.

## Testing

Focused automated coverage must include:

- orphan catalogue traversal, symlink, mutation, XML/DOCTYPE/entity, duplicate,
  corruption, cancellation, and resume cases;
- missing-history direct, RAR, and multipart source recovery;
- duplicate-payload collapse and distinct-payload ambiguity;
- stable archive identity across published filename changes;
- refusal of same-size ambiguity and cross-release candidates;
- idempotent reconciliation with zero queue/history/DAV writes;
- refusal to replace an existing exact mapping;
- plan rejection for every non-exact status and count mismatch;
- deterministic batch partitioning and resume without duplicate submissions;
- master/final coverage math, source drift, and the 90% gate;
- create-only link application, exact-size revalidation, journals, validation,
  and rollback;
- an end-to-end fixture covering retained history, orphan recovery, renamed
  archive output, batching, reconciliation, planning, apply, validation, and
  aggregate accounting.

PR CI remains authoritative for its normal lanes. Production acceptance requires
fresh runtime evidence in addition to tests: deployed commit/image markers,
healthy containers, readable mounts, terminal batch ledgers, exact plan counts,
link and coverage audits, and bounded media probes.

## Acceptance criteria

- Issue #13 is linked from the implementation PR.
- The existing canary reconciles to 30/30 exact without resubmission.
- The full dry run accounts for every source link and projects at least 90%
  uniquely recoverable coverage before import starts.
- Every imported batch terminates, reconciles exactly, and has an immutable,
  checksummed, fully actionable plan.
- `/mnt/plex` remains unchanged by migration tooling.
- Every created `/mnt/plex2` symlink has the same relative path as its final
  `/mnt/plex` source and targets a size-matching InfiniDysk `.ids` regular file.
- Final validated `/mnt/plex2` coverage is at least 90% of the fresh final
  NzbDav-link snapshot, with every remainder explicitly classified.
- Seek, throughput, and representative `ffprobe` results are retained.
- Plex and Arr remain unmodified and `/mnt/plex2` remains unregistered.
