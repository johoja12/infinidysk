# NzbDAV native-cache regression audit

Audit date: 2026-09-23. InfiniDysk baseline: `daf4c5f7b6b25a441f2802ba5a0405bbfb644b48`.
NzbDAV donor snapshot: `ebfffdb04cbbc61e1c32c2c4eff6653ccf68e1d6` from the local
`johoja/nzbdav` checkout. These are source snapshots, not assertions about the
currently deployed versions.

The review screened the 207 non-merge commits reachable from the donor snapshot
that touch `backend/Services/Cache/` or `backend/Streams/NativeCacheStream.cs`,
then examined representative fixes and regressions for the failure classes below.
Historical `#` numbers refer to the donor's old Bitbucket issues; `NZBDAV-` keys
refer to its Jira project. This is not an exhaustive live Jira issue review.

## Confirmed defect and correction

InfiniDysk's `NativePrefetchExecutor.WarmAsync` trusted the catalogue to skip
committed ranges and report completion. A file could have 100% catalogue coverage
after its data file was removed, truncated, changed, or its volume detached.
All four regressions failed against the baseline: three jobs returned successfully
without repairing the data, and the offline-volume job returned successfully
instead of deferring. This is the same failure class as donor #826 (`5845c177`).

Warming now verifies existing blocks in the requested range before calculating
missing coverage or declaring completion. It uses the stream's existing admitted
buffer and cancellation ownership. Missing, truncated, or hash-mismatched blocks
are invalidated and filled through the ordinary reservation and provider-budget
path. An offline/inaccessible volume defers the job. Valid blocks are not fetched
from Usenet again, including in read-only folders. Verification yields between
blocks when foreground playback or a pause takes priority.

This changes warming completion, not the playback hit path: playback already
checked each cached block's hash. Existing statistics remain catalogue snapshots;
they do not promise a volume is online or that external changes have been scanned.

**Cost:** warming now reads and hashes the already-cached portion of its requested
range. A repeated whole-file warm can therefore read the whole file from the NAS
even though it uses no provider payload. This adds local I/O and must be measured
on the intended storage before treating this as a throughput improvement. Ordinary
cache hits still perform their existing per-block checks.

## Comparison with historical failures

“Guarded” means the current implementation has a specific protection and focused
regression coverage. It is not a guarantee against every storage or scheduling fault.

| Historical failure and donor evidence | InfiniDysk result | Code and regression evidence |
| --- | --- | --- |
| Gapped chunks returned unwritten bytes: #487, `977f5c9b` | Guarded. Only explicitly indexed blocks can be hits; sparse file length is not coverage. | `NativeCacheStore.ReadBlockAsync`; `SparseHole_IsNeverAHit`; `UnalignedRange_CrossesCachedHeadMissingMiddleAndCachedTailWithoutChangingBytes`. |
| Writes crossing a chunk boundary skipped their trailing data: #178, `6ad18d1f` | Guarded by complete aligned block publication. A cross-boundary request is served in separate blocks. | `WriteBlockAsync`/`ValidateOffset`; `IncompleteBlock_IsRejected`; unaligned mixed-source regression above. |
| Concurrent consumer/read-ahead used one mutable fallback position: #181, `2dc714b8`; multi-parent corruption #193/#210 | Different ownership model. Native streams have private source state; shared playback has one pump; same-block fills coordinate. No donor stream-reuse pool is imported. | `NativeCachedStream`; `SharedStreamEntry`; `ConcurrentMisses_CoalesceOneVerifiedFill`; `NativeSharedGenerationTests`. |
| Overlapping writes or range publication exposed incomplete data: #588, `f2870953`; NZBDAV-1174, `be6f825f` | Guarded. Writers serialize; complete data and journal records are flushed before the catalogue publishes a block. | `NativeCacheStore.WriteBlockAsync`; `NativeCacheConcurrencyTests`; `NativeCacheJournalTests`. |
| A pooled buffer was returned twice or reused while still active: NZBDAV-1174, `be6f825f` | Donor `BufferedSegmentStream` is not the native source wrapper. Current draining transfers buffer ownership once; detached NAS operations retain their buffer/admission until actual completion. | `MultiSegmentStream.DrainSegmentAsync`; `SegmentBufferPoolTests`; `CancelledBackgroundIo_RetainsAdmissionUntilUncancellableIoCompletes`. |
| Synthetic zeros/short padding became trusted cache data: #610, `ca2a70df`; untrusted seek writes #628/#629, `8c98e294`/`33996b44` | Guarded. Every read contributing to a block must supply cacheability evidence. Synthesized padding and untrusted placement do not qualify. | `ICacheReadEvidence`; `CacheReadEvidenceTests`; `NativeStorage_AdmitsOnlyCompleteVerifiedFinalFileBytes`. |
| Only a prefix was validated while the remainder was wrong: #991–#994, `3f519519`; NZBDAV-1149, `23255eba` | Guarded at native admission: complete buffered article validation, trusted segment geometry for every contributing segment, and full-block hashing. | `MultiSegmentStream.MatchesCacheGeometryAsync`; `NativeFidelityIntegrationTests` includes later wrong-offset, missing, and short segments. |
| Encryption/multipart wrappers applied to already-final bytes or lost validation: `30cf195b`; NZBDAV-1090, `39f0c41e` | Native wraps the final-content factory, after multipart assembly/decryption. Evidence propagates through these wrappers. | `DavContentStreamFactory`; `AesDecoderStreamTests`; `DavMultipartFileStreamTests.NativeReads_PropagateVerifiedEvidenceAcrossTrustedParts`. |
| Catalogue said complete while actual data was missing: #826, `5845c177` | **Reproduced and fixed in this audit.** | `NativePrefetchTests.Warm_RechecksCachedDataBeforeDeclaringCompletion`; `Warm_OfflineCachedVolumeCannotReportCompletion`; valid-head/corrupt-middle/valid-tail regression. |
| Eviction raced active streams or writes: NZBDAV-1146, `535dd44e`/`75c4e0ae`/`0e5c23d3` | Guarded by file activity leases and serialized writer/evictor ownership; pins and active entries survive eviction. | `Clear_RetainsActiveAndPinnedEntries`; `NativeCachePressureTests`; scan concurrency regression. |
| Inflated totals, sparse logical size, or reservations exhausted disk: #829, `b4b7b2b8`/`852a3e1b` | Physical allocation and conservative pending/warm reservations are tracked separately from logical coverage. Shared-filesystem folders preserve the largest reserve. | `NativeFileSystem`; `NativeCachePressureTests`; `SuccessfulScan_ReconcilesCrashReservationExactly_AndIsIdempotent`. |
| Bad/offline folder blocked all placement or wrote into an unmounted local directory: #381, `e556094d` | New files can choose another eligible folder. A replaced registered root fails identity checks. Existing entries remain assigned to one folder; see limitations. | `FolderPriority_AndQuota_ChooseOneDestinationPerFile`; `ReplacedLiveRoot_EvenWithCopiedMarker_IsNeverWritten`; restart mount tests. |
| Interrupted writes, lost range sidecars, or missing metadata overstated valid data: NZBDAV-1174/1189, `be6f825f`/`cf27d9cd` | Data, journal, and directory flushes precede publication. Explicit reconciliation verifies journals; torn tails are not imported. | `NativeCacheJournalTests`; `ExplicitScan_ImportsVerifiedJournalIntoNewCatalogue`. |
| Old data survived source remapping or repair publication: NZBDAV-1188/1189, `03cd5c38`/`0941fc03` | Cache identity includes source metadata hash and source-specific repair revisions. Active responses reject mixed generations. | `NativeCacheService.WrapAsync`; `ContentRevisionTrackerTests`; `RepairRevisionStoreTests`; `NativeSharedGenerationTests`. |
| Playback attribution disappeared on full/partial cache hits: #888/#959, `0d5f9330`/`db305aea`; NZBDAV-1170, `2f4fba96` | DAV item attribution occurs before opening native content, even when no source is opened. Verified Plex sessions and raw read hints remain separate. | `BaseStoreStreamFile.OpenFinalStreamAsync`; `PlexPlaybackRegistry`; `NativePrefetchIntegrationTests`. |
| Scanners/preview reads triggered excessive full-file warming: NZBDAV-1147, `2ecffe9d` | Automatic warming and raw-read hints are opt-in. However, when raw-read hints are enabled, an active scanner read can qualify; there is no sustained-sequential threshold in that policy. | `PrefetchSettings`; `PlexPrefetchService.RunAsync`; remaining limitation. |
| Purging native cache left stale/corrupt data in rclone VFS: #985/#1010/#1011, `d82f9777`/`c15c4d76`/`eea16641` | **Remaining integration limitation.** Native eviction/invalidation does not purge external rclone payload caches. | `NativeCacheStore.EvictCoreAsync`/`InvalidateBlockAsync`; current `RcloneClient` exposes metadata forgetting, not a connected native data-purge operation. |
| NFS locks/stalls caused hangs or resource growth: `e200401d`, `d0fa519b`, `47bc1b3d` | Foreground native I/O/fill waits are bounded and can fall back. Buffer ownership survives cancellation. A stuck writer can still stop background writes/maintenance; real NFS behavior remains a hardware acceptance item. | `NativeCachedStream.CacheIoAsync`; `NativeCacheProbeDeadlineTests`; native initialization and stalled-I/O tests. |

## Remaining limitations

1. **One folder per cached file.** A partly cached file does not spill into another
   folder when its original folder fills or goes offline. Source playback can
   continue, but warming may defer despite free capacity elsewhere. No automatic
   migration, striping, or replica selection is implemented.
2. **Native invalidation stops at the server.** rclone and players may already
   hold bytes from an older source revision or degraded playback. Repairing native
   storage cannot recall those bytes. A coordinated client-cache invalidation
   design and a real rclone acceptance test are still needed for that guarantee.
3. **Bounded admission can bypass a valid cache.** The default 32 MiB native budget
   allows eight admitted 4 MiB stream buffers. If all admissions are held, another
   ordinary stream opens its source directly; a required-native warmer defers.
4. **Playback writes are opportunistic.** There is one serialized writer. If it is
   busy, foreground playback can decline a cache fill. Writes are not a native
   write-behind pipeline, and each cold block may involve read-ahead and durable
   filesystem I/O before serving the requested bytes.
5. **Background maintenance can wait on the NAS.** Request timeout is not proof
   that a filesystem call stopped. Playback fallback is bounded; an uncancellable
   writer can retain its slot and delay new writes, probes, and eviction. Folder
   status also checks filesystem identity without a per-folder deadline; a stuck
   mount can delay the cache status API. This path was identified from code, not
   reproduced against a failed production mount.
6. **Read-activity warming can include scans.** Keep the raw-read trigger disabled
   when warming should be driven only by Plex/manual policies. Enabling it does
   not establish verified Plex playback or a sustained viewing session.
7. **Integrity checks prove byte consistency and mapped article evidence.** They
   do not prove that a correctly checksummed media file is semantically playable,
   nor guarantee unavailable Usenet gaps can be recovered. Source maps without
   adequate proof can stream without being admitted into native cache.
8. **Hardware behavior needs its own evidence.** Synthetic tests do not establish
   NFS/SMB power-loss durability, cold/warm throughput, or long-run behavior with
   concurrent playback across a 50 TB cache. Use the isolated
   [native-cache canary](native-cache-prefetch.md) for those claims.

## Validation

The original-source reproduction ran four failing tests in `NativePrefetchTests`
for deleted, truncated, hash-mismatched, and offline data. The corrected suite also
covers provider-budget admission, read-only hits, valid neighboring blocks,
requested-range boundaries, foreground pause, cancelled I/O buffer ownership,
and a byte-for-byte cache/source/cache playback range.

Result: **335 passed, 0 failed, 0 skipped** in the focused suite on Linux with
.NET 10 and the repository's musl rapidyenc build. The original four-case failure
and corrected runs were captured as TRX reports. No full production/NAS soak was
performed. Repository CI remains the authority for the other required lanes.

Run the focused audit suite with the repository's .NET 10/native build environment:

```sh
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NativeCache|FullyQualifiedName~NativePrefetch|FullyQualifiedName~NativeFidelity|FullyQualifiedName~NativeShared|FullyQualifiedName~NativeFileSystem|FullyQualifiedName~CacheReadEvidence|FullyQualifiedName~ContentRevisionTracker|FullyQualifiedName~RepairRevisionStore|FullyQualifiedName~AesDecoderStream|FullyQualifiedName~DavMultipartFileStream|FullyQualifiedName~Prefetch|FullyQualifiedName~SegmentBufferPoolTests|FullyQualifiedName~StreamFidelityTests'
```

The audit used temporary local test directories. It did not verify or repair the
contents of the production cache, alter live cache settings, or deploy the fix.

Setup-wizard impact: no new configuration, schema, or setup behavior. The fix
applies to existing warming operations.
