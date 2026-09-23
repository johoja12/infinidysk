# Native cache follow-up validation

This report covers issues [#38–45](https://github.com/johoja12/infinidysk/issues/38)
and builds on the verification correction in PR #36. Tests use disposable local
storage; hardware outage and throughput acceptance remains tracked in #46.

## Implemented policies

| Issue | Change | Boundary |
| --- | --- | --- |
| #38 | An unpinned partial warm can restart on eligible storage when the original folder is over quota or offline. Old allocation is retained in a retirement ledger; scans cannot resurrect that replica. | This refetches through provider budgets rather than merging replicas. Pinned/read-only entries stay put. Fully cached offline files defer. Ordinary playback alone does not initiate relocation. |
| #39 | Native-backed WebDAV modification times track source metadata and source-specific repair revisions, persist across restart, and remain unchanged on normal capacity eviction. | rclone must refresh directory metadata and reopen. Existing player buffers and open handles cannot be recalled. |
| #40 | With at least 8 MiB configured, one 4 MiB buffer is reserved for overflow readers and shared per read. Warming cannot consume it. | Saturated or stalled overflow reads have a one-second admission deadline, then source fallback. A 4 MiB configuration shares its only slot. |
| #41 | Playback returns the current verified block before its durable cache write completes. The next block/disposal drains the write or transfers buffer ownership until completion. | One pending write per admitted stream; full-block source read-ahead remains. Coverage still publishes only after durable flush. |
| #42 | Status checks have a one-second deadline and at most one outstanding probe per folder. Unknown status is explicit. | A timeout does not stop an OS syscall; root handles remain owned until it returns. |
| #43 | Writers are isolated by filesystem and maintenance by folder. | Folders sharing a filesystem retain serialization for shared-space accounting. A stalled syscall still owns its gate. |
| #44 | Raw-read warming requires at least 64 MiB of contiguous reads over 30 seconds, with no idle gap over 15 seconds. Seeks reset evidence. | Sustained sequential scanners can still qualify. Raw reads remain unverified, opt-in, and separate from Plex evidence. Existing cooldown and provider budgets remain. |
| #45 | Concurrent verification of the same revision/block shares in-flight work. Completed verification results are never reused. | A later whole-file warm still reads/hashes all cached blocks to detect external damage. |

Restarting a partial warm retains the previous payload and its quota charge until
the replacement is fully covered and no file leases remain. Confirmed Clear can
also reclaim retired entries when inactive. Interrupted or partial replacement
may retain old allocation longer; this is conservative accounting, not free space.
The local catalogue gains additive retirement and modification-time tables.
Back up `/config` before upgrading; preserve the local catalogue alongside payload
roots. The existing folder-total ledger also retains retired allocation for older binaries,
but they cannot display or reclaim retired entries; clear those entries before downgrading. No application EF migration is added.

Setup-wizard impact: no new keys or controls. Existing Native mode, buffer budget,
folder policy, and opt-in raw-read trigger apply; no wizard version change.

## rclone reproduction

Tested isolated `rclone v1.75.1` (`rclone/rclone` image digest
`sha256:45401ad7410db1d67ffdb58e19059ad20b0d8e0285a60e38bbec55cc1019c7a5`),
serving a disposable local VFS over WebDAV with `--vfs-cache-mode full`,
`--vfs-fast-fingerprint`, and `--read-only`. Fast fingerprint models a source
without a usable content checksum; this is a VFS reproduction, not an end-to-end
InfiniDysk deployment test.

1. Read a 1 MiB file containing `A`; rclone stores its payload.
2. Replace it with the same length of `B`, preserving its modification time.
3. Call `vfs/forget` for the file; the response reports success.
4. Reopen: the returned SHA-256 still matches `A`.
5. Advance source modification time, forget metadata, and reopen: returned bytes
   match `B`.

| Payload | SHA-256 |
| --- | --- |
| Original and stale reopen | `4e29ad18ab9f42d7c233500771a39d7c852b200baf328fd00fbbe3fecea1eb56` |
| Repaired source and reopen after mtime refresh | `5ae9782017a68037004b2bf806c77d324db4d915ed3725d84eb3121b2ad16061` |

[rclone documents `vfs/forget`](https://rclone.org/rc/#vfs-forget) as directory-cache
invalidation. It is not a payload-purge API. Do not substitute remote delete/purge
operations, which operate on source files. The correction publishes a changed
source fingerprint through modification time, using ordinary client revalidation.
First observation on upgrade or catalogue rebuild uses a fresh modification time
so legacy creation-time fingerprints revalidate; this can refill client caches once.
Refresh every affected mount's metadata, then reopen playback. An unavailable RC
endpoint delays refresh until normal expiry or a manual retry; source repair does
not depend on RC being available. Native disk-only corruption never admits bad
bytes to a hit and does not itself change the source revision.

## Focused regression coverage

- Stalled status probes: deadlines, repeated polling, cancellation, and recovery.
- Independent-filesystem writes/probes while another writer is stalled.
- Cross-folder warming: full/offline source, pins, byte equality, retired allocation,
  scan resurrection protection, active leases, and cleanup.
- Twenty open streams reading cached data with a source factory that throws.
- Write-behind returns bytes before a blocked write and retains admission until
  actual publication, including disposal timeout.
- Same-size source repair changes modification time; repeated reads, normal
  eviction, and restart keep it stable.
- Concurrent verification shares a read; later corruption is checked again.
- Fast scans, repeated previews, seek resets, idle resets, and sustained activity.
- The missing/truncated/corrupt/offline warm regressions from PR #36 remain.

## Manual I/O measurement

Run with the repository .NET/native environment:

```sh
dotnet run --project backend.Benchmarks -c Release -- --native-cache-io-report
```

The report seeds a disposable 64 MiB payload and compares three serial warms with
three batches of four concurrent warms. It records elapsed time, actual integrity
read bytes, unshared read bytes, and zero provider bytes. OS caches are warm;
these timings do not establish NAS or cold-disk throughput. Repeat on the intended
isolated hardware before sizing production concurrency. #46 owns that acceptance.

Measured on the local Linux/.NET 10 musl test host (warm OS page cache):

| Requests | Actual integrity reads | Without sharing | Elapsed | Provider bytes |
| --- | --- | --- | --- | --- |
| 3 serial | 192 MiB | 192 MiB | 499 ms | 0 |
| 3 batches of 4 concurrent | 192 MiB | 768 MiB | 489 ms | 0 |

This single local sample demonstrates reduced duplicate reads for overlapping
requests, not a universal latency improvement or a NAS performance guarantee.
