# Diagnosis: find the discriminating evidence

## Preserve the identity chain

For each failure record the Arr instance/media IDs and download ID, or Plex rating key; current library file ID/path; all parts and versions; mounted link target and extracted `.ids` UUID; ID name/subtype/blob/NZB/history/grab identifiers; observed errors and probe times. Related UUIDs are not interchangeable.

Readiness failures are different from completed history waiting on Arr. An empty ID download queue does not clear blocked imports, failed previews, or damaged library sources. Source/preview entries created before a parser fix or source replacement may be stale.

## Bounded media checks

Run from the failing consumer's mount, with a timeout and modest concurrency. Prefer private JSON/error artifacts with credentials redacted.

```bash
timeout 30 ffprobe -v error -probesize 2000000 -analyzeduration 2000000 \
  -show_entries format=duration:stream=codec_type,codec_name,width,height \
  -of json /exact/reviewed/file

timeout 40 ffmpeg -nostdin -v error -threads 1 -filter_threads 1 \
  -ss 60 -i /exact/reviewed/file -map 0:v:0 -frames:v 1 \
  -an -sn -f null - -progress pipe:1
```

Choose a time inside the real duration; also sample the reported failure region. Require a decoded frame in progress/output and inspect error output for corruption or read failures, not exit code alone. FFmpeg can exit zero after reporting an invalid EBML region. A seek beyond that region may succeed even when contiguous decoding through it fails. Explicitly select the real video stream: an MP4 may contain an attached cover-art video with larger dimensions. Validate that selection from stream metadata rather than universally assuming the first stream.

A good header can coexist with missing middle/tail articles. A 60-second sample is not a full decode. Conversely, a timeout alone does not prove bad media: transport pressure, admission waits, GPU failures and mount health require separate checks.

## Cases that change the decision

| Observation | Check next / conclusion |
|---|---|
| “Unable to determine if file is a sample” | Probe actual bytes and streams. The message is not proof of a sample or a parser defect. |
| EBML invalid/zero bytes | Correlate the exact offset. ID may have preserved offsets by filling a missing-article gap; current `430`/all-provider misses distinguish this from a posted invalid header. |
| MP4/M4V “moov atom not found” | Inspect head/tail availability, source layout and container offsets. Archive head-only validation cannot guarantee trailing metadata exists. |
| RAR ordering suspect | Check volume headers/order, data offsets, continuation lengths and encryption. RAR5 first-volume header number can be `-1`; filename order alone is insufficient. Compare the deployed aggregator's plan against source bytes. |
| Valid article/yEnc/PAR2 CRC | Establishes transfer/proof integrity, not valid MKV/MP4 semantics. A CRC-valid source can contain zero media or invalid EBML fields. |
| Correct source but bad mounted reads | Compare bounded direct ID WebDAV ranges to mount reads, then verified posted/decrypted source bytes. A mount/cache difference is not automatically an archive parser bug. |
| Same invalid bytes from source and WebDAV | Regrab the posted media; separately investigate why validation admitted it. A diagnostic header edit is a control, not permission to patch live media. |
| Missing articles on one provider | Check eligible fallback providers, exclusions/storage groups, attempt outcomes and verification. Do not claim globally missing from a single server miss. |
| Arr “episode/movie not found in grabbed release” with readable media | Reconcile scene and canonical numbering, release/grab context and every expected episode. Repair context when supported; do not regrab solely from this warning. |
| Paired episode / multipart movie | Verify duration, file count, plot titles and correct IDs. A `Part 1` can be half of a special; an unsplit release title can still contain half a movie. A healthy three-hour file is not a complete six-hour movie. |
| ISO cannot import | An ISO may be healthy but incompatible with the Arr/media-file workflow. Find a supported media-file release without changing the global profile. |
| “Wasn't grabbed by Arr” | Failed handling can skip manually submitted/migrated downloads. Reconcile current library state before cleanup or search. |
| Preview old filename or bundle hash | Compare cached `file_path`, `media_parts_info`, live Plex API/catalogue and actual link targets. Retry after correcting verified stale metadata, not the healthy media. |
| Preview queued but never dispatched | Check priority-only admission, rate limit, mount protection, worker slots and active progress. Queue acceptance is not execution. Avoid enabling the whole backlog to run a small reviewed batch. |
| New selection repeats bad post / ISO | Re-review its real output and identity; compare regrab provenance, not just indexer/release title. Reject incompatible repeats within the approved scope. |
| Apparent minimum-size rejection | Verify actual duration and episode count against the runtime used by the size gate. Do not silently lower profiles or override a blocklist. If authorized review supports a one-off exception, record the evidence, validate output before registration, and preserve existing companion files. |
| Indexer download returns HTTP error | Verify configured client headers, authentication and redirects; report an access/configuration problem separately from missing articles. Never print redirect values, including URL paths. |

## ID code-backed RCA

Read the deployed revision, not just local `main`. Useful starting points:

- `backend/Queue/QueueItemProcessor.cs`, `FileAggregators/PlannedImportOutputs.cs`, RAR/7z aggregators.
- `backend/Queue/PostProcessors/FinalMediaReadinessValidator.cs`, `ArchiveMediaReadinessProbe.cs`, `backend/Utils/MatroskaHeaderValidator.cs`, `backend/Config/ConfigManager.cs` for category/readiness policy.
- `backend/Streams/`, `backend/Clients/Usenet/`, `libs/UsenetSharp/`, `libs/SharpCompress/` for geometry, fallback, draining/decryption and proof failures.
- `backend/Clients/RadarrSonarr/`, `backend/Services/ArrMonitoringService.cs`, health/repair and watchdog services for import/recovery behavior.

Include a reproducer using the exact validated source condition, expected versus observed output and the smallest failing code path. Distinguish a proven defect from an audit hypothesis. No config, native library, release or deployment conclusions should rely on an old session's snapshot alone.
