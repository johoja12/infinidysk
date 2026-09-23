# Native Cache direct-WebDAV canary plan

Status: isolated first pass started on 2026-09-23. See
[the run results](native-cache-webdav-canary-results-2026-09-23.md) for passed
checks, deviations, and remaining gates. No production canary has run.

## Goal and scope

Exercise two or three imported media files through the backend WebDAV endpoint,
without rclone, and prove that Native Cache tracks the bytes actually read while
full playback and seeks continue to work. Use three files when available: a
straightforward movie, an episode, and a multipart/encrypted file used by this
installation. Record each item's ID, exact WebDAV path, size, file subtype,
Native Cache key and generation, and source revision. Choose files with enough
size to leave several 4 MiB integrity blocks between sampled ranges. Keep the
total cold-read payload within an agreed provider budget.

The first run should use an isolated instance built from the intended commit,
with its own `/config` and writable cache root. Never mount one catalogue/cache
root writable in both the test instance and production. Record the image digest
and commit before testing. The production image observed on 2026-09-23 was
`f136cebd`, while repository `main` already contained later Native Cache
follow-ups; recheck both versions before interpreting results. Repeat a bounded
production canary only after the intended image is deployed.

## Preparation and isolation

1. Verify Native mode is active, initialization succeeded, the selected cache
   folder is writable and online, and there is enough quota/free space for the
   planned reads. Record `/api/native-cache` counters and folder status.
2. Pause or disable automatic/manual warm jobs and other clients for the chosen
   items during each deterministic range phase. Do not invoke Media Library
   Prewarm, Plex playback, or rclone for those items until the phase allows it.
3. Use the authenticated backend WebDAV address on port 8080 and each item's
   internal `/.ids/...` path. Keep credentials in protected operator files;
   exclude request headers and tokens from captured logs. Confirm the request
   reaches backend directly, with no rclone container or mount in the path.
4. For each item, list `/api/native-cache/entries?folderId=...` and identify its
   exact key. Run the file eviction checks below before the first cold read. Do
   not clear a shared folder to simulate file eviction.
5. Record a baseline for the selected item's cache coverage, hit/miss counters,
   provider bytes, and any concurrent warm jobs. If spontaneous warming changes
   the baseline, isolate the item and restart that phase.

## Per-file eviction checks

Use the authenticated cache catalogue's **Evict file** action and the equivalent
`POST /api/native-cache/operations` request with `operation: "evict"`, `folderId`,
`cacheKey`, and matching `confirmCacheKey`. The response queues a job; it does
not prove deletion. Poll `/api/native-cache` until that job completes with
`result: 1`, then refresh the catalogue and assert that the chosen key has no
entry or verified ranges. Record the selected item's source WebDAV path before
eviction and confirm that a subsequent cold GET can still open it.

On a disposable cache instance, run these negative cases before the read matrix:

| Case | Expected outcome |
| --- | --- |
| Missing or mismatched `confirmCacheKey`, malformed key, or wrong folder | Request is rejected or job fails; selected and neighboring entries remain intact. |
| Pinned selected entry | Eviction job fails; unpin, retry, and confirm only that key is removed. |
| Active playback/read lease on selected entry | Eviction job fails; playback continues, then eviction succeeds after the reader closes. |
| Read-only/offline folder | No deletion; job reports failure and catalogue/bytes remain recoverable. |
| Repeat an already completed eviction | No other key is removed; the old key stays absent. |

Seed at least one neighboring file in the same folder and compare its key,
verified ranges, cached bytes, and readability before and after every targeted
eviction. Test the confirmation dialog and disabled pinned/read-only action in
the UI. Check the job's final state and error text; a queued or running state is
not a pass. Run fault cases only on disposable storage. For the production
canary, use only the confirmed successful path and inspect the neighboring
entry without injecting storage faults.

## Read matrix

Native Cache stores complete 4 MiB integrity blocks. For partial reads, compare
coverage against block-aligned expectations, allowing only documented read-ahead
or other measured requests. Capture HTTP status (`200` for a full GET, `206` for
a satisfiable range), `Content-Range` for range requests, `Content-Length`,
received bytes, elapsed time, and a SHA-256 of every sampled response. A `HEAD`
request may establish length but must not count as a cache fill. Wait for
in-flight cache writes to finish before judging final coverage.

| File | Cold direct-WebDAV requests | Expected coverage before playback | Seek and replay checks |
| --- | --- | --- | --- |
| A: full | One complete `GET` to EOF | 100% verified for the current key/revision | Replay beginning, middle, end; compare hashes and cache-hit/provider counters. |
| B: half | `Range: bytes=0-N`, where `N = 4 MiB × floor(size / (8 MiB)) - 1` | Only fetched blocks verified; the remaining half stays missing | Play from start, seek within cached half, then seek into the cold half and verify new blocks appear. |
| C: gap | Separate head and tail range GETs, each at least 4 MiB, leaving at least two complete 4 MiB blocks untouched in the middle | Two separated verified regions; middle gap remains absent | Play head, seek into middle gap, then seek to cached tail and back across the boundary. |

For B and C, fetch a small crossing range around each cached/missing boundary.
Compare its bytes with repeat reads after the gap fills. Full and partial responses
must match their declared lengths; no zero padding, truncation, or stale revision
is acceptable. Re-list verified ranges using `/api/native-cache/ranges?key=...`
after each phase and compare the sum of range lengths with `VerifiedBytes` for
the same key. Repeat selected ranges once warm: their hashes must match the cold
responses and provider payload should not increase for covered blocks.

## Playback and seek check

After each deterministic snapshot, open the same direct WebDAV URL in a player
that issues HTTP range requests. Play long enough to decode video and audio;
seek forward and backward within cached data, into a cold area, and across a
cache boundary. For the full-file case, seek near EOF. Save the player's HTTP
and decoder errors, buffering/stall duration, seek completion time, and the
backend's stream/cache counters. Distinguish source/provider failure from a
cache fault; rerun a failed position after eviction only when it will help
isolate that cause.

## Pass criteria and evidence

- Targeted eviction affects only the chosen key; a second cached item's entry,
  ranges, and bytes remain intact. Pinned/active entries are retained with a
  clear job failure, and wrong-folder or invalid keys cannot delete anything.
  The source WebDAV file remains available after eviction and can be cached
  again under its current revision.
- Full GET reaches 100% verified coverage. Half and gap reads publish only
  verified fetched blocks; the planned gap is still missing before the cold
  seek. Coverage updates after cold seeks and persists after stream close.
- `GET`/range status, lengths, byte hashes, and current-revision identity are
  consistent on cold and warm reads. Repeated covered reads use Native Cache
  hits without new provider payload for those blocks.
- All three player sessions decode and seek without persistent stalls, HTTP
  failures, truncation, incorrect bytes, or cache-induced playback errors.

Save one evidence bundle per file: build/config identifiers, redacted eviction
request and final job state, target/neighbor catalogue snapshots, WebDAV request
log, range hashes, coverage snapshots, provider/cache counter deltas, player
log, and deviations. Mark each matrix row pass/fail and record any retry. Stop
on an unexpected deletion, source-revision change, quota/free-space issue, or
unbounded provider use; do not broaden eviction or clear production cache as a
workaround.
