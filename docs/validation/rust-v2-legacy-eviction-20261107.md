# Compatible v1-cache eviction qualification

2026-11-07. Source: the commit containing this report, based on `b9220aa8`.
Tracking: [#240](https://github.com/johoja12/infinidysk/issues/240).
See [fork-only parity audit](../rust-v2-fork-parity-audit-20261107.md),
[writer format qualification](rust-v2-compatible-cache-writes-20261107.md), and
[NAS handover packet](../rust-v2-cache-handover-runbook.md).

This adds normal cache policy management, separately from migration. It does not
activate eviction, alter production settings, stop a service or delete production
cache data. Migration still preserves the original cache storage and payloads in
place. Production and actual NAS qualification remain open.

## Opt-in policy

Read-only mode never mutates original metadata or payloads. Compatible writable
mode now refreshes verified-read access times using v1 five-minute buckets. The
bounded asynchronous queue and memo coalesce repeated hits; metadata writes do not
delay verified playback. Pending or dropped recency prevents new victim selection.
Dropped updates keep eviction held for operator review; they do not invalidate
playback bytes. Shutdown requests a bounded drain; handover must first drain all
readers and confirm recency is settled. Read-only sessions do not persist original
access times, so v1 age cleanup needs review on read-only handback.

Eviction is disabled unless a compatible-write folder explicitly supplies
`eviction`. Example fragment inside `native_cache.legacy.write`:

```json
{
  "chunk_mb": 8,
  "folders": [{
    "id": "original-folder-id",
    "priority": 10,
    "quota_bytes": 1000000000000,
    "min_free_bytes": 100000000000,
    "eviction": {
      "max_age_days": 30,
      "high_percent": 90,
      "low_percent": 80
    }
  }]
}
```

Omit `eviction` to preserve all payloads. Age zero disables age removal while
retaining pressure policy. Watermarks require `1 <= low < high <= 100`.
Policies require restart and existing exclusive writable cache ownership. They
are not enabled by importing the old configuration or enabling compatible fills.

Oldest eligible entries are selected by v1 `Access,Key`. Age and quota/free-space
pressure are distinct triggers. Pressure continues to the low watermark with room
for another block; pins are never overridden when targets cannot be reached.
Folders on the same device honour the strictest peer free-space reserve for
both fills and eviction. A reserve larger than reported filesystem capacity holds
eviction rather than purging toward an impossible target. Separate NAS exports
may hide a shared backing pool; do not sum their capacity without qualification.
Manual pin 1, health pin 2, dirty/pending entries and all active read/warming leases
are protected. Admission conservatively protects every generation of the same
item during deletion. Direct I/O and fills also hold an entry guard. A stale entry
handle cannot invalidate a fresh copy refilled in another folder.

## Durable deletion protocol

1. Persist an exact key/folder/volume/generation identity and unique nonce in the
   private Rust catalogue's additive `legacy_evictions_v1` table.
2. Under item admission and the entry guard, atomically move accounting to original
   v1 `RetiredEntries` and remove `Entries`/block coverage. The original accounting
   triggers retain the bytes in `FolderTotals`. No original schema is changed.
3. Validate the registered volume and manifest. Rename only that entry directory
   within its original shard to `rust-evict-<nonce>` and sync the shard. This is an
   eviction tombstone, not relocation or cache conversion. The original C# scanner
   skips retired entries and non-key directory names, preventing resurrection.
4. Delete only validated regular private leaves. Unknown files, symlinks, hard links,
   changed volumes and ambiguous recreated entries hold the operation. Directory
   operations are descriptor-relative and cannot traverse outside the registered
   entry. The manifest is removed last. Sync the entry/shard before capacity credit.
5. Remove the matching retired accounting only after physical absence is proved.
   Record the event and clear the private intent in one Rust transaction. Retry
   cannot credit space twice or discard ownership of a conflicting recreated copy.

Per pass: at most 32 recovery intents, 64 candidates per folder, and 4096 payload
leaves per entry batch. Keyset/intent cursors prevent active or held rows from
starving later candidates. A four-second cooperative pass budget stops between
operations; a five-second caller deadline does not cancel real I/O. One owned
maintenance task retains its guards/ownership until physical and metadata work
ends. A stalled kernel/NFS operation can exceed the budget and hold maintenance.
At 256 outstanding intents, new eviction staging stops for reconciliation.

Partial large-entry deletion keeps its intent, manifest and retired accounting for
the next pass. Read/fill admission cannot expose a partially deleted generation.
No future fills use an original key/folder that is still retired. Recovery operates
only on owned intents matching the original registered volume; it does not reclaim
pre-existing v1 retired copies or guess from a directory prefix.

## Recovery and handback

Back up the private Rust catalogue together with the matching original catalogue
and volume registrations. Both are needed to recover owned deletion intents.
Do not restore stale metadata over newer state or remove owner/volume files.

Stop new policy work before handback. Confirm outstanding intents and recency are
settled before starting v1 or an older Rust image. Disabling the policy pauses
further cleanup; it does not undo a staged eviction. Lost/mismatched metadata,
unknown leaves and recreated entries need explicit review. Do not clear holds or
bulk-delete `rust-evict-*` directories based only on their names.

Only one application owns the cache. The v1 background evictor must not run
alongside v2. No automatic deletion policy is part of initial in-place migration.

## Validation

Artifacts are retained in `scrap/upstream-sync-20261107/eviction-parity/`.

- Formatting and warnings-denied all-target workspace Clippy passed.
- API schema generation and TypeScript/Vite production build passed (bundle-size
  advisory only).
- Original C# harness built with zero warnings/errors. Its two sparse/chunked
  eviction/scanner cases passed: no resurrection and unrelated bytes unchanged.
- The final Rust application test separately read the original C# fixture in
  place with providers disabled: one passed.
- `RUST_TEST_THREADS=4 cargo test --workspace --locked`: 816 passed, zero
  failures, 11 default ignores. The two new ignored entries are explicit
  process-child/export helpers; process-death recovery is exercised by an active
  parent test. Log: `workspace-qualified.log`.

Generated tests cover opt-in defaults, access freshness and dropped telemetry,
manual/health pins, pending writes, whole-session and direct-I/O protection, quota
and free-space pressure, low-water hysteresis, candidate fairness, independent
folder errors, stale handles after refill, unknown/linked files, recreated metadata,
large chunk batches, cancellation and recovery at seven error/process-death
boundaries. Existing compatible read/write tests remain part of workspace validation.

The original unmodified C# reader/scanner at `9c104b59` checks sparse/chunked
fixtures with an eviction interrupted after coverage withdrawal but before payload
rename. It must not resurrect the retired entry, and unrelated bytes must still
match. This is a separate explicit fixture case, not a claim about live NAS.

The first full run exposed an old allocation assertion in the warming test:
`st_blocks` changed by eight sectors after in-place I/O. The test now checks
unchanged device, inode, length and full content hash. Filesystems may change extent
allocation without copying or replacing the cache file. Failed logs are retained.

Remaining fork gaps: legacy administration/physical diagnostics #249 and complete
scanner/compaction/old-retired-copy/stale health-pin lifecycle #250. Actual NFS
loss/recovery, mixed playback/warming/eviction performance and production activation
remain #238/#237 gates. The old `b9220aa8` image does not qualify this new code.
