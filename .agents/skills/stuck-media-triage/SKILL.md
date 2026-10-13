---
name: stuck-media-triage
description: Investigate stuck Sonarr/Radarr imports, failed Plex Preview generation, and damaged InfiniDysk sources. Correlate queue, library, preview, and Usenet evidence to distinguish an ID defect from missing articles, bad media, stale metadata, or import mapping errors, then recommend or perform authorized repair, retry, or regrab.
---

# Stuck media triage

Trace a failure to an exact file before deciding to repair ID or replace media. A queue warning or failed preview is a symptom, not a diagnosis. Default to investigation when the user asks to check/review; perform recovery when authorized in the current conversation. Existing recovery authority persists, but creating this skill does not authorize future mutations.

## Collect current evidence

Read [collection.md](references/collection.md) for the host discovery hints, API/schema pointers, and the read-only `scripts/collect.py` helper. The helper writes private snapshots; it does not probe media or change live state.

- Inspect all enabled Arr instances, ID queue/history, health/repair and prefetch state, and the actual migration/replacement runner if one exists. Record UTC capture time, deployed revision, scope, and unavailable sources.
- For Preview, read the running application's database and worker state. Check whether progress advances; `updated_at` can describe catalogue metadata rather than worker start. Do not use an old database copy or a login-page response as evidence of current jobs.
- Group Arr rows by `(instance, downloadId)`, preserving every movie/episode ID. Count releases separately from affected episodes. Delayed RSS candidates without a download ID are not ID downloads; old `added` values alone do not prove a stall.
- Reconcile each suspicious row with current `hasFile`, file ID, path, scene title, and symlink target. A warning may persist after a replacement has imported.

## Establish identity and cause

Read [diagnosis.md](references/diagnosis.md) for the decision matrix and discriminating probes.

Build the chain:

`Arr media ID -> library/queue file -> mounted symlink -> ID .ids UUID -> DavItem/blob/NZB -> posted articles`

For previews, retain the Plex rating key and map **each current media part/version** through the same chain. Compare `file_path`, `media_parts_info`, current Plex catalogue/API parts, and the actual filesystem. A cached path or bundle hash can survive a replacement. Nullable `HistoryItemId` and `ArrDownloadId` are normal for some migrated files; use the remaining identity evidence rather than inventing a join.

Use bounded probes from the consumer mount first, then direct ID WebDAV and source/provider evidence when necessary. A successful header probe or frame sample does not certify whole-file playback. Probe the reported failing offset or time when a preview fails later in a file.

Choose a supported disposition:

| Disposition | Evidence needed |
|---|---|
| ID defect | Correct source bytes/order/geometry produce different or invalid served bytes, or the deployed parser/validation/streaming path reproduces the failure |
| Missing/damaged articles | Exact source segments miss or fail verification across eligible providers; correlate current ID logs, range/verification evidence, and consumer failure |
| Bad posted media | Source bytes match served bytes and are themselves invalid, even when article CRCs pass |
| Mapping/container limitation | Media decodes, but grab context, scene numbering, paired episodes, multipart movie, or ISO import expectations disagree |
| Stale/retryable preview state | Current file/part/source is readable and the recorded error references an old source/path or transient worker failure |
| Unresolved | Identity, source verification, or the failing region is not yet established |

Multiple causes can coexist: bad source content and an ID validation gap are separate findings. Do not assume every EBML error is a RAR bug, or every read error warrants deleting media.

## Recovery and automation

When recovery is authorized, read [recovery.md](references/recovery.md). Preserve unrelated and completed mappings; choose the smallest operation supported by the diagnosis. Do not copy historical release IDs, credentials, or ad-hoc mutation scripts into a new run.

For an ID defect, create a source-backed reproduction and fix on an isolated branch under the repository's commit/PR rules. An upstream PR, merge, or deployment requires the corresponding current-session authorization. For bad media, quarantine only the verified bad library link/record, reject the exact bad release where appropriate, and search for a suitable replacement. For readable media, prefer context repair or preview retry over regrab.

Automation must use a finite reviewed manifest, durable intent/accepted/verified records, bounded retries, and an explicit deadline. Reconcile an uncertain write before any retry. Observe **execution**, not merely accepted API requests: a queued preview can be excluded by priority-only scheduling, and a successful search can choose another ISO, half a movie, or the same damaged post.

## Deliver the result

Report release and episode counts, exact correlated IDs, evidence supporting each cause, proposed/completed actions, preservation checks, and remaining uncertainty. Separate search acceptance, import verification, preview queue admission, and finished BIF generation. If a monitor remains active, name its scope, deadline, status artifact, and stopping conditions; do not claim queued work is complete.

Never log credentials, cookies, signed URLs, or raw redirect locations. A provider can put credentials in the **path**, not just the query. Keep secrets inside the authenticated process and redact errors before printing, saving shareable reports, or filing a PR.
