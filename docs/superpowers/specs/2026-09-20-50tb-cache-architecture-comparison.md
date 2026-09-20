# Native versus segment caching at 50 TB

Status: source-backed recommendation and sizing analysis, not a benchmark result.
This extends the [integration design](2026-09-20-smart-prefetch-native-cache-design.md)
and [implementation plan](../plans/2026-09-20-smart-prefetch-native-cache.md).

## Recommendation

For a Plex-oriented 50 TB media cache, prefer **native final-file ranges**, with
multi-folder management, one data file per media generation, small integrity units, a
persistent indexed catalogue and bounded in-memory state. Preserve Segment as
an alternative cache mode. Do not run both. Do not copy either current cache
implementation unchanged and declare it suitable for 50 TB.

Confirmed user workload: HDD/NAS, mostly whole movies/episodes. For sizing, treat
50 TB as decimal 50,000,000,000,000 bytes of useful cached media; 50 TiB is about
10% larger. Exact storage topology, RAM and concurrent stream counts remain
unknown. Benchmark the actual storage before fixing write batching, concurrency,
headroom or memory budgets.

Adding multiple folders to the existing Segment cache fixes placement/capacity
configuration, but not its catalogue, object count, startup and eviction costs.
A *redesigned packed segment cache* could scale; that is a substantial storage
engine change, not just a folder UI enhancement. For this workload, it still
retains final-file reconstruction/decryption and lacks natural media-level
coverage, which native storage provides directly.

## Current-source findings

Target snapshot: InfiniDysk `a809fa2e`. Donor snapshot: NzbDav `f6c14875`.

- `backend/Clients/Usenet/SegmentCacheNntpClient.cs` stores one decoded body
  plus a `.h` JSON header per Message-ID hash, in 256 two-hex-character shards.
  `LoadIndex` recursively enumerates files, reads headers, and constructs a
  `ConcurrentDictionary` entry for each valid cached article. Startup runs in
  the background, but that does not remove its I/O/RAM cost or time to readiness.
- `EvictWhileLocked` uses `_index.OrderBy(...).ToList()` under the eviction lock.
  At tens of millions of entries, a complete sort/materialization for capacity
  pressure is a structural scaling problem. More roots alone do not remove it.
- The donor `backend/Services/Cache/CacheService.cs` uses 64 MiB default chunks,
  file-level database metadata, multiple folders and batched eviction candidates.
  Its in-memory file cache has a 100,000-entry cap; cleanup sorts that bounded
  cache. These are better starting properties, not proof of acceptable 50 TB
  behavior. Retain the corruption/lease/regression safeguards from the design.
- The initial proposal's one immutable file per 4 MiB extent is also too costly
  as a default for this target capacity. This analysis supersedes that physical
  layout: logical validation blocks do not need separate filesystem files.

## Object-count model

Model: fully occupied useful payload, no cross-entry deduplication, no compression,
and uniform unit sizes. These are arithmetic examples, not measured average Usenet
article sizes. Partial packing, archive overhead and metadata change the totals.

| Layout | Payload units at 50 TB | Approximate filesystem files |
| --- | ---: | ---: |
| Current segment layout, 768 KiB/article | 63.6 million articles | 127.2 million bodies + headers |
| Current segment layout, 1 MiB/article | 47.7 million articles | 95.4 million bodies + headers |
| Initial native proposal, 4 MiB/extent file | 11.9 million extents | 11.9 million data files plus metadata |
| Native 64 MiB containers | 745,059 containers | About 745,000 data files plus per-media metadata |
| Native 256 MiB containers | 186,265 containers | About 186,000 data files plus per-media metadata |
| One sparse file per media, illustrative 20 GB/media | 2,500 media items | About 2,500 data files plus metadata |

Counts for container layouts assume well-filled containers; scattered ranges in
many media files increase them. Do not preallocate full containers on sparse reads
and then call allocated capacity useful cached content. The one-file option has
different corruption, locking and eviction trade-offs, so file count alone does
not select the winner.

At 1 MiB/article, an *illustrative* 200 bytes of resident index state per article
would already be 9.5 GB, before sort temporaries, dictionary overhead not included
in that estimate, reader buffers or the application. Real .NET object/string costs
must be measured. The proposal must not load all articles or all range records
into RAM in either design.

Inodes and directory entries are real metadata objects, so a low payload-byte
overhead does not imply cheap lookup, enumeration or deletion. Filesystem limits
and costs depend on format and configuration; these numbers do not prove a hard
filesystem limit. See the [Linux ext4 inode documentation](https://www.kernel.org/doc/html/latest/filesystems/ext4/inodes.html).

## Workload comparison

| Concern | Extended/packed segment cache | Refactored native file cache |
| --- | --- | --- |
| Safe integration effort | Reuses existing article validation and transport boundary; packed storage/indexing is still new work | Requires exact source-map/fidelity and repair invalidation proof above archive/decryption |
| Repeat Plex playback | Avoids NNTP for hits; still runs reconstruction and applicable decryption | Delivers final bytes without redoing archive/decryption for hits |
| Partial small reads | Natural article units; may fetch more than requested | Exact final ranges; small validation units inside larger containers |
| Cross-file reuse | Same Message-ID can be reused across consumers/references | Same content revision is shared; no speculative title-based deduplication |
| Space efficiency | Can deduplicate shared articles, but may retain archive bytes unused by playback | Stores final requested bytes, but can duplicate equivalent content under unrelated identities |
| Coverage and prefetch UI | Requires article-to-file/archive mapping to estimate final coverage | Exact cached ranges, media percentage, per-file warm/evict are native concepts |
| Failure recovery | Independently validated articles are a strong boundary | Must fence repaired/remapped/decrypted bytes and synthetic gaps |
| Large HDD/NAS storage | Feasible with packed objects + disk index; individual-article layout is costly | Coalesced per-file containers fit sequential playback and per-media retention |
| Generic queue/repair tools | Article reuse useful outside media playback | Final-file cache does not replace provider diagnostics or repair patches |

The preference for native is an engineering inference from source architecture
and workload, not a universal claim that segment caching cannot scale. If the
actual workload is predominantly small disjoint article reads, repeated imports,
or shared article reuse across many files, a packed segment design becomes more
attractive. Prototype it behind Segment mode as a comparator if measurements
show those properties; do not implement two active caches to hedge the choice.

## Required large-cache storage design

1. **Separate validation from physical layout.** For the confirmed mostly-whole
   media workload, prefer one data file per media generation, sparse while partially
   populated, with an exact verified range map. Keep incomplete files internal;
   only the native reader may interpret valid ranges, never expose sparse holes
   as playable data. Track range checksums in units up to 4 MiB; stream writes
   with bounded buffers. Completion marks the same file complete without another
   full-file copy. Published ranges are immutable; repairs create a new generation.
   Compare 64/256 MiB containers if the NAS lacks useful sparse-file behavior or
   partial-file tests expose unacceptable allocation/locking costs. That alternative
   is still Native mode, not a second active cache. Persist layout per generation.
2. **Crash-safe range publication.** Serialize a container's unpublished-gap writes,
   flush data, then durably append checksummed range-commit records referencing
   physical offsets/hash/revision. Publish read coverage only afterward. Never
   overwrite published valid bytes in place. Interrupted/invalid journal tails
   cannot advertise coverage. Checkpoint manifests incrementally and replay only
   bounded dirty tails. Preserve the existing lease and fidelity requirements.
3. **Persistent searchable metadata.** Keep a rebuildable catalogue on local SSD,
   indexed by folder, content generation, access/eviction bucket and coverage.
   Use a dedicated auxiliary SQLite store on local disk (SSD preferred) with batched single-writer updates and
   bounded reader/page caches; do not add cache churn to the main application DB.
   The pack/journal/manifest evidence remains authoritative. A missing catalogue
   triggers rate-limited recovery; foreground source reads remain available.
   No full-tree scan or all-entry sort on a normal startup or quota eviction.
4. **Local metadata even with NAS data.** SQLite WAL requires same-host shared
   memory and does not work over a network filesystem; it also has one writer
   at a time. Configure a verified local metadata path and bound transactions,
   reader duration and WAL checkpoints. Do not put the WAL catalogue in an NFS/SMB
   cache root. See [SQLite's WAL documentation](https://www.sqlite.org/wal.html).
   If local metadata cannot be provided, reject enabling that profile with an
   actionable configuration message; do not silently adopt unsafe locking.
5. **Batched placement and eviction.** Place an entry on one folder, honoring
   per-folder priority/quota/age/read-only state. Use indexed bounded candidates
   and high/low watermarks, e.g. trigger at 90% and drain toward 85% of configured
   quota as a candidate policy, not a universal disk-fill recommendation. Keep
   an independent filesystem free-space reserve. Batch access timestamps and
   retire whole cold media generations initially; do not sort every range.
6. **Budget all overhead.** Quotas include allocated physical bytes, metadata,
   in-flight writes, obsolete generations and compaction scratch. Avoid full-size
   copy-on-write for each tiny range. Share one filesystem free-space budget across
   roots on the same volume. Two folders on the same disk are not two I/O devices.
7. **Readiness for a large retained set.** Separate speculative and demand-admitted
   retention so hub scans cannot evict the whole recently watched working set.
   Expose keep/pin versus evictable policy if 50 TB means a retained library rather
   than a rolling cache; refuse new warm admission when protected items fill quota.
   A cache is still refetchable storage, not an archival or backup guarantee.

The 2 GiB free-space floor in the small testing defaults is not an operating
recommendation for a 50 TB filesystem. Size reserves for each actual filesystem's
policy, worst-case concurrent writes and temporary overhead. Similarly, the test
daily speculative budget is a canary limit, not a plan to fill 50 TB quickly.

## Evidence required before calling it 50 TB-ready

- Generate catalogues representing 50 TB at 768 KiB/1 MiB articles and
  64/256 MiB containers with bounded tooling; do not allocate 50 TB or manufacture
  millions of files on the shared workspace. Record row counts and synthetic status.
- Test startup readiness, one-entry lookup, paginated UI, quota eviction, journal
  replay and root loss. Verify indexed plans, bounded batches and metadata memory
  independent of total payload capacity. Test concurrent catalogue checkpoints
  with foreground streaming and prefetch pressure.
- On dedicated representative disks, compare a smaller *real, fully written*
  corpus at 64 and 256 MiB containers and one-file sparse storage. Exercise
  many small ranges and mostly-full media separately. Measure physical allocation,
  IOPS, throughput, cache-hit startup/seek latency, RAM and inode use.
- Sparse apparent size and synthetic metadata validate different properties;
  neither proves sustained performance at an actually occupied 50 TB. State that
  limitation until a staged real-capacity deployment supplies the evidence.
- Include permission/disconnect/reconnect for each folder, partial corruption,
  interrupted publication, catalog loss/rebuild, disk-full and eviction under
  playback; require exact output hashes and no active Segment+Native combination.

For illustration, 50 TB holds approximately 2,500 files averaging 20 GB, or
12,500 averaging 4 GB. That is why a media-file layout is a particularly good fit
here. The actual size distribution still governs counts and metadata costs.

This recommendation changes the storage foundation in Tasks 3–5 and adds a
large-catalogue gate in Task 10. Multi-folder UI and full configurable Plex parity
remain required regardless of the measured native file/container geometry.
