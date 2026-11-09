# Smart Prefetch activity UI comparison

The previous port added routes and settings but did not reproduce v1's activity
and history workflows. The screenshot report exposed that completed attempts were
mixed into the queue, diagnostics dominated the page and filters were absent.
This change compares the retained `origin/v1` activity and prediction components,
using generated fixtures rather than production media names.

| v1 workflow | v2 change |
| --- | --- |
| Queue status, running count, completed today, budget | Four summary tiles; running count uses active rate records only |
| Active queue separate from history | Activity excludes terminal outcomes |
| Search and running/queued/deferred/paused filters | URL-backed file/source/viewer search and state filters |
| Warming vs playback gap-fill history | Separate history types |
| Repeated warming runs grouped by file and range | Exact item/revision/ranges grouping with individual attempts retained in Details |
| Source and viewer context | Named source/profile badges, preserving all grouped attempts |
| Fully/partially cached history filters | Current source-bound coverage; completion alone never implies cached bytes |
| Pagination | 20 or 50 activity/history rows; 25 prediction rows |
| Prediction search, viewer and cache-state filters | Search/profile/source filters and cache-state count buttons |
| Detailed queue diagnostics | Collapsed diagnostics and repair follow-up |

Coverage metadata is loaded in batches of at most four requests. Results are
accepted only for the requested source revision and file size; failures and
changed revisions remain unknown. Grouped history preserves saved attempt
outcomes and never adds byte counts from different attempts together. Historical
actions are shown only when their exact job/run/state still exists in the queue.

One backend gap remains distinct: v1 persisted detailed playback cache-miss cause
codes (buffers full, busy block, slow storage and write failure). Rust's current
backfill journal does not retain those codes. The outcome-reason filter uses
recorded warming outcomes and does not invent a playback miss cause. This requires
separate data-plane instrumentation in #257; it is not silently counted as UI parity.

Validation covers responsive light/dark layouts, filtering/pagination, grouped
attempts, changed-revision coverage, bounded metadata concurrency and existing
cache/Plex/settings workflows. Production acceptance is separate from generated
fixture evidence.
