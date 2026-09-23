# Native Cache direct-WebDAV canary: isolated first pass

Date: 2026-09-23. Status: **partial pass; production canary pending**.

This run used the deployed `413ebdb9bef66ad1a975001d52eb4f278190025d`
image (`sha256:afa926e93eb23ac56f5d58add5f37191e4dddde8caed58d3975f17dbe7c91cca`)
on `nuc-1`. A disposable container had its own PostgreSQL copy, `/config`, and
Native Cache root. The backend WebDAV endpoint was bound to host loopback on
port 18080; no rclone mount was attached. Automatic prefetch, Plex/Arr
integrations, and repair were disabled in the test configuration. The cache
folder reported Native mode, online, writable, and zero entries at baseline.
The test containers were stopped and removed after the run. Their cloned
database, configuration, cache contents, and test credentials were deleted.
The redacted evidence JSON remains root-only at
`nuc-1:/opt/infinidysk/canary/native-cache-20260923/evidence/`.

## Selected media

| Case | DAV item ID | Size | Source subtype | Result |
| --- | --- | ---: | --- | --- |
| A, full episode | `9e834842-86ef-4edd-ade7-595ca23197d6` | 367,192,064 B | 203 | Full transfer stopped at the provider-traffic guard; 100% media coverage unproven. |
| B, half episode | `d492cfc8-c2a5-4150-a864-62f2da85c174` | 765,578,092 B | 203 | Half read, replay, cold boundary seek, and playback passed. |
| C, gapped movie | `2c13b084-e571-4cbf-b517-7a48bf839150` | 1,552,476,658 B | 201 | Head/tail gap, cold middle seek, replay, and playback passed. |

All three files were opened by their internal `/.ids/...` WebDAV paths. A `HEAD`
returned `200` and the catalogue size for each. The cache entries recorded
current `v2` generations and SHA-256 cache keys in the private evidence bundle.

## Eviction and read results

- On the disposable cache, missing/mismatched confirmation, malformed key,
  and wrong folder requests returned `400`. A pinned entry's eviction job
  failed. Unpinning then evicting that exact key completed with `result: 1`;
  repeat eviction failed as already absent. The neighboring entry retained
  its key and 4 MiB verified coverage throughout.
- A long, held WebDAV response kept the selected entry leased. Eviction failed
  with “File is currently in use; retry after playback stops,” and the entry
  remained. A shorter, fully buffered response did not retain the lease long
  enough for this test, so the long response is the meaningful result.
- With the disposable folder configured read-only, eviction returned `400` and
  both target and neighboring entries remained. An offline-storage fault and
  the browser confirmation dialog were not exercised.
- A full `GET` of a small associated image returned `200`, delivered all
  289,675 declared bytes, and reached 289,675 verified bytes (100%). This
  validates full-file handling for a small object, not a full media stream.
- B's `Range: bytes=0-381681663` returned `206`, declared and delivered
  381,681,664 bytes, and left 385,875,968 verified bytes. The extra 4 MiB was
  measured read-ahead. Provider usage rose by 551,574,618 bytes for this
  phase. A covered 4 MiB replay had one cache hit and zero additional provider
  bytes. A crossing range fetched a missing block; its replay had the same
  SHA-256 and zero additional provider bytes.
- C's 8 MiB head and tail ranges each returned `206` with exact declared and
  received lengths. The unaligned tail covered three integrity blocks, leaving
  17,361,394 verified bytes across separated regions. The middle stayed
  missing until a cold seek fetched it; replay SHA-256 matched and provider
  bytes did not increase. For B and C, the sum of listed range lengths equaled
  the entry's `VerifiedBytes` at the deterministic snapshot.
- A's initial full request was interrupted after progress stalled at 12 MiB
  verified while provider traffic continued to rise. A later 8 MiB range
  across that position returned correctly in 3.77 seconds. This leaves the
  cause of the first stall unresolved; the full-media coverage criterion did
  not pass. The interrupted request has no complete-response hash.

## Decoder playback

FFmpeg read through a local loopback proxy that injected WebDAV authentication
and forwarded requests directly to the test backend. It decoded five seconds
of video and audio per position and exited successfully for B at the start,
3 minutes, and 10 minutes; C at the start, 3 minutes, 46 minutes, and 94
minutes; and A at the start and 45 minutes. C's start emitted non-monotonic
DTS warnings, and A's late seek emitted an invalid audio packet warning.
Those warnings were not traced to source media versus cache, so decoder
cleanliness is not claimed. The HTTP request log and player stderr are in the
private evidence bundle.

## Remaining gates

1. Diagnose A's interrupted full transfer and complete a **full media** GET
   with 100% verified coverage, exact byte count, and a warm replay hash. Do
   this within a new provider-traffic budget; the first pass stopped rather
   than forcing repeated whole-file downloads.
2. Exercise the disposable offline-folder fault and the UI confirmation and
   disabled-state checks. Recheck decoder warnings against the same source
   without Native Cache if they recur.
3. Only after the isolated pass meets the plan's criteria, run its bounded
   production canary on the deployed image. This run did not evict or stream
   through the production Native Cache.
