# Native cache and Plex Smart Prefetch

[since unreleased testing branch](https://github.com/johoja12/infinidysk/pull/4){ .nzbdav-since }

!!! warning "Testing branch"

    This feature is under development. Automated correctness tests are not proof of
    throughput, durability, or recovery on your NAS. Use a separate configuration,
    cache folders, and container ports. Do not share writable metadata or cache roots
    with a production instance. Back up `/config` before testing an upgrade.

## Choosing storage for 50 TB

For HDD/NAS storage that mostly retains whole movies and episodes, choose **Native**.
It stores one sparse final-media data file per source revision, with verified 4 MiB
coverage and a local indexed catalogue. When its folder has capacity, completing a partially cached movie fills
that same file; it does not assemble millions of article files into another copy.

**Segment** remains useful for repeated article/range reads on fast local storage.
Adding more Segment folders alone would not remove its article-file cardinality,
turn eviction into whole-movie retention, or avoid archive/decryption work on hits.
The two implementations are alternatives, not cache tiers.

50 TB decimal is about 45.5 TiB. Set folder quotas below actual usable capacity and
keep free-space reserves for the NAS and other applications. Keep metadata on local
SSD if available; never place the SQLite catalogue on NFS/SMB. Neither cache is a
backup or a guarantee that uncached Usenet source bytes remain available.

## Configure cache mode and folders

Use **Settings → Native Cache** [since unreleased](https://github.com/johoja12/infinidysk/issues/76){ .nzbdav-since }. Select Off, Segment, or Native.
Settings show configured and active modes separately until a restart activates the
change. A Native failure falls back to ordinary source streaming, never to Segment.
Explicit mode changes retain inactive cache files for rollback.

### Cache sizing [since unreleased](https://github.com/johoja12/infinidysk/issues/75){ .nzbdav-since }

**Minimum file size to cache** defaults to 100 MiB. Files below the threshold stream directly from the source during playback and are ineligible for background warming. Set it to 0 to admit smaller files. **Cache chunk size** defaults to 64 MiB (4–256 MiB in 4 MiB steps). New entries use separate chunk files of this size; each chunk contains independently verified 4 MiB blocks so playback can still fill on demand without reading a whole chunk. Background warming reserves one chunk at a time. Existing entries retain their recorded layout and stay readable when the setting changes. Save either setting and restart to apply it to new cache entries. An environment-owned `NZBDAV_CONFIG__CACHE__NATIVE__MIN_FILE_MB` or `NZBDAV_CONFIG__CACHE__NATIVE__CHUNK_MB` value is shown read-only in Settings. These are advanced controls; the setup wizard does not ask for them.

The NzbDav fork also shows cache enablement, write-provenance logs, clear-all, and folder controls on its Native Cache page. InfiniDysk already provides mode selection, folder management, and clear operations. The fork's write-provenance switch is a diagnostic for its legacy implementation and is not carried over. Existing InfiniDysk entries use a single sparse data file; new entries record their chunk size in the manifest and catalogue. Changing the setting never rewrites an existing entry.

## Browse native cache [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

Open **Native Cache** in the main navigation to see capacity, verified file coverage,
24-hour block hit rate, source misses, active cache writes, and recent activity.
The **All files** tab searches live cache entries across folders and opens a verified
range list for each file. A coverage percentage can hide gaps: only listed verified
ranges are cache hits, and reads across gaps use the source when available.
**Recently evicted** records confirmed pressure, idle-age, folder-rollover, and folder-clear removals
from the time this feature is enabled; earlier removals cannot be reconstructed.

Folder allocated size includes retired allocation waiting for safe cleanup, while
the file count includes only live entries with verified bytes. Legacy entries may
initially show an ID until the local catalogue fills display names in the
background. The 24-hour traffic view begins with the first observation after
upgrade, and an unclean shutdown can lose its most recent unflushed seconds.
Browser queries read local catalogue metadata and never warm files or enumerate
NAS payloads. Folder configuration and maintenance remain under **Settings →
Streaming**.

The upgrade adds tables and columns to the local native-cache catalogue under
`/config`. Back up `/config` before upgrading. The schema change is additive, but
older binaries do not understand browser history or the new telemetry fields.

Add each container-visible native folder with its name, enabled/read-only state,
storage type, placement priority, byte quota, minimum free bytes, maximum age, and
high/low eviction watermarks (90%/80% defaults). Eviction starts at the high watermark
and continues in bounded batches toward the low watermark; active and pinned entries
are protected. A protected full folder declines new fills instead of exceeding quota.
Higher placement priority is considered first. Age zero means no age limit. A
read-only folder can serve existing verified content but cannot accept new fills.
Probe folders before warming; use Scan for explicit reconciliation, not every boot.
Clear requires confirmation of the selected folder ID and targets owned cache files.
The catalogue browser shows verified coverage, source generation, bounded range pages,
and pinning against eviction. Coverage is a catalogue snapshot, not proof that a volume
is currently online; playback checks identity and hashes again.

Probe reports detected filesystem capabilities separately from the HDD/NAS/SSD hint.
A successful write/fsync/read-back probe is not proof of NAS power-loss durability.
Native initialization and probes have bounded caller waits. Automatic journal
checkpoints process a bounded exact-entry queue; they do not scan the whole library.
At the hard journal bound, new fills decline until checkpoint/reconciliation succeeds.

Folder ownership is anchored to the validated directory and filesystem identity.
A replaced mount, symlink, or copied ownership marker is not trusted automatically.
Restore the original mount, or configure a new folder ID and explicitly scan its
owned cache data. Do not remove markers or ownership locks to force a folder online.
Foreground native I/O has a bounded wait and falls back to source reads; a stuck
operation retains its buffer allowance until it actually finishes, so repeated NAS
stalls cannot allocate unlimited buffers.

The buffer allowance (`cache.native.writer-mb`) is split into 4 MiB block slots
(32 MiB gives 8). A stream holds a slot only while it fills or verifies one block:
once the block is ready the slot returns and the stream keeps just that block to
play out, and a stream paused mid-fill gives its slot to a waiting stream. A response
that starts on a block the cache does not have opens its source and reads its first
bytes before taking a slot, so connection start-up never holds one. Many open streams
therefore share a small allowance, and warming never takes the last slot. When a block cannot get a slot within about a
second, or a cache probe or another stream's fill of the same block takes too long,
only that block streams from the source; it is scheduled for backfill and the
following blocks are cached as usual. `nzbdav_native_cache_buffer_slots{state}` shows
`free` and `held` slots and the `serving` blocks streams keep; raise the allowance if
`free` often reads zero.

| Configuration key                | Purpose                                                             |
| -------------------------------- | ------------------------------------------------------------------- |
| `cache.mode`                     | `off`, `segment`, or `native`                                       |
| `cache.native.folders`           | Bounded JSON array of native folder settings                        |
| `cache.native.metadata-path`     | Direct local path for rebuildable catalogue and warming state       |
| `cache.native.writer-mb`         | Native block buffer allowance in MiB (4 MiB slots); 32 by default   |
| `smart-prefetch.settings`        | Validated policy settings JSON; automatic warming is off by default |
| `plex.accounts` / `plex.servers` | Persisted Plex credentials, saved servers, and exact mappings       |

Native folders, metadata location, buffer allowance, and cache mode require restart.
Policy changes use Apply; queue actions and dedicated Plex Save/Disconnect actions
take effect immediately. Save/restart Native mode first, then enable Smart Prefetch;
Apply performs a bounded write probe and requires a healthy writable root. Disable
Smart Prefetch before changing cache mode or restart-required folder settings (the
disable and storage change may be saved together). Environment-owned values stay pinned through ManagedSetting;
use the [headless naming rules](headless.md#naming-algorithm) for overrides. Credentials
are masked in UI/config responses. Protect `/config`, which holds the actual tokens.

## Connect Plex

Use browser sign-in to obtain a Plex PIN authorization URL, complete sign-in at Plex,
then return to InfiniDysk. Pending requests can expire, be cancelled, or retried.
Select an existing account, optionally switch Plex Home users with their Home PIN,
and discover owned/shared server connection candidates. Select the intended server
card and use **Save selected server**; the success notice confirms that it is persisted
and immediately available to Smart Prefetch. **Refresh** only repeats discovery and
never changes saved configuration. Manual URL/token setup, path mappings, testing,
and saved-server edits remain under **Advanced server configuration**.

Multiple saved servers are independent. Account disconnect removes the local
account and disables servers linked to it; it does not revoke authorization at Plex,
delete cached media, or disable independently configured manual-token servers.

Map Plex media paths by exact directory prefix to either an imported DAV path or a
container-visible local library path. Similar-looking prefixes do not match. Enable
**Allow mapped local library files** only when local symlinks/STRM files resolve to
an exact imported DAV item. Ordinary local files are skipped: this option does not
create another rclone warmer or an arbitrary filesystem cache.

## Prediction results [since unreleased](https://github.com/johoja12/infinidysk/issues/177){ .nzbdav-since }

Open **Smart Prefetch → Prediction results** to inspect predicted episodes and live
whole-file cache coverage. The latest successful prediction snapshot appears on
reopening the tab while a shared background refresh checks Plex. Its timestamp,
refresh progress, and any failure remain visible. Failed or incomplete refreshes
retain previous results; a successful empty refresh clears them. Snapshots are
invalidated when accounts, servers, or policies change.

Persistence across restarts [since unreleased](https://github.com/johoja12/infinidysk/issues/181){ .nzbdav-since }: the last complete snapshot is saved under `/config` and restored with its original timestamp when the configuration still matches. Restored results are marked **Previous results** until a fresh refresh succeeds. When Plex is unreachable or does not respond, the tab explains that it will retry automatically and shows saved predictions if available. A first-time installation or changed configuration has no saved results to show until Plex responds. Corrupt or unwritable snapshot files produce a warning; in-memory results, cached media, and existing warming jobs remain available.

Each viewer is marked **Verified next-unwatched** or **Chronological candidate**.
Unverified viewers receive a specific explanation and a link to Plex connections
when appropriate. Account names alone never authorize watched-state queries;
InfiniDysk verifies the authenticated account's identity and access to that server.

**Not in library** means the prediction has no resolved imported file. **Cache
unavailable** means a mapped file exists but Native Cache cannot report coverage.
Loading and failed cache requests have their own states and show no percentage.
**Not cached** requires a successful query confirming zero cached bytes. Cache
coverage continues refreshing independently of prediction snapshots, including
files excluded from warming by the size cap. Viewing results does not enqueue work.

## Select policies and inspect work

The normal Smart Prefetch view contains only the master switch, movie and TV
eligibility, and the daily provider-payload budget in decimal GB. Fixed smart
defaults handle verified playback, next-episode prediction, whole-file warming,
playback pausing, and conservative concurrency. Expand **Advanced settings** to
change signals, prediction thresholds, schedules, queue resources, or warming
ranges. **Customized** means at least one policy value differs from those defaults.
**Reset to smart defaults** restores policy behavior without disabling Smart
Prefetch or removing selected Plex users, hubs, or collections.

The normal view also shows compact Plex connection status. Library/user/source
selection is available under **Plex libraries and sources**. Open the separate
**Smart Prefetch** page from the sidebar for live queue activity and recent warming
history. Its **Advanced warming controls** section contains policy preview, manual
warming, and retry controls. The Settings activity section links to that page.
The Settings sections stay collapsed until needed so the everyday setup remains limited to
Enable, Movies, TV episodes, and the daily GB budget.

Choose a saved server and watching profile, then pick the **Movies** or **TV shows**
tab. Switch on a library to use its recommended sources: movie libraries select
**Recently Added**, while TV libraries select **On Deck** and **Continue Watching**
when Plex provides them. These defaults apply only the first time a library has no
existing source choices. Expanding a library shows its hubs and collections side by
side (the first 10 of each, with **Show all** for longer lists); collections remain
off by default. Use **Customize** for a source's item limit, excluded TV show IDs, and
preview.

The source picker [since unreleased](https://github.com/johoja12/infinidysk/issues/160){ .nzbdav-since }
also has a filter box that searches every hub and collection across libraries, with
matches grouped by library, and an **On** list of every enabled source labelled with
its library; remove one there with ×. Each source appears once: a collection Plex
also shows on the home screen is listed under hubs with a *collection on Home* tag,
and server-wide home hubs that repeat a library's hubs are left out of **Home screen
hubs**. Finish with **Apply source changes**, which saves only Smart Prefetch and
leaves unrelated Settings drafts untouched.

Movie and TV section switches, and each library switch, pause that scope without
discarding its source choices, limits, or exclusions. Turning the scope back on
restores the same configuration. **Refresh Plex catalogue** updates available
libraries and sources without rewriting the current draft. A saved source that Plex
does not return remains visible under **Saved but not currently available** so it can
be reviewed or disabled deliberately. Selections belong to stable
server/library/source identities, not display names, and users remain scoped to the
server. History lookback, minimum distinct episodes, confidence threshold, cooldown,
queue-ahead count, and episodes per show remain under Advanced settings. History
confidence is bounded distinct-episode evidence, not a claim that a viewer will watch
the prediction: a show watched for exactly the **minimum distinct episodes** scores 0.5
and twice the minimum scores 1, so with the default 0.5 threshold meeting the minimum
is enough (a minimum of 1 predicts the next episode after one watched episode), while a
higher threshold asks for more episodes [since unreleased](https://github.com/johoja12/infinidysk/issues/156){ .nzbdav-since }.
Watch history identifies each episode's show from Plex's history response, which names
the show only by its metadata key.

Next-unwatched filtering requires a verified credential for the initiating user.
Connect selected Plex Home accounts so their server-specific credentials can be
discovered. Another user's server token never supplies that user's watched status.
Without verified credentials, candidates are chronological and explicitly marked
watch-status unknown. Show sources use matching selected users, otherwise the linked
server account. Source previews distinguish mapped, unmapped, excluded, and items
requiring episode/metadata expansion; Policy Preview validates imported eligibility.

Realtime Plex sessions and raw DAV read activity are separate signals. Raw reads
may be scans and never become verified Plex playback. Verified session evidence
expires; failed polls cannot renew it. A failed session/history source does not block
the same server's healthy hubs/collections.

Use whole-file warming for retained movies/episodes. Partial warming estimates a
resume byte window from Plex playback time; it is only a hint, not a container seek
index. Optional minimum warming fills head/tail ranges. Policy Preview shows proposed
imported files, source/reason, and ranges without enqueueing them. It is bounded and
may report a partial result when a source is unavailable or its time window expires.

All producers use the same bounded warming queue and existing low-priority NNTP
admission. Native cache hits do not re-download the content. Configure workers,
per-job connections, per-file and daily byte caps, and playback pause. A daily budget
of zero means unlimited, not disabled; the initial default is 10 GB/day. Queue actions include manual/bulk imported
item IDs, sync, pause/resume, cancel/retry, and priority changes; priority never bypasses
foreground admission. Whole-file jobs report verified committed whole-file coverage,
not bytes merely read from providers. Range jobs report their own range first (see
below).

**Urgent jobs overtake far-ahead warms** [since unreleased](https://github.com/johoja12/infinidysk/pull/202){ .nzbdav-since }.
A running whole-file job normally keeps its worker until it finishes. When a
higher-priority job is waiting for a worker, such as the next episode another viewer is
about to start, the whole-file job steps aside if every viewer of its file already has
at least 10 minutes of playback cached ahead (or nobody is watching it). It keeps its
verified coverage, spends no retry, and resumes about 15 seconds later once a worker is
free. A job for a file that is playing never steps aside before its viewer's position
has been seen. Small playback backfill ranges already run in their own slot.

**Warming history** [since unreleased](https://github.com/johoja12/infinidysk/issues/117){ .nzbdav-since }
shows each finished job's average warming speed and active duration, for example
`14.2 MB/s · 3m 05s`. Speed counts only the bytes that job fetched and committed,
divided by its running time; queued, deferred, and paused time is excluded. The list
header shows the median speed of the visible jobs, which helps spot a slow provider or
a busy cache disk. Jobs recorded before this release show "—". Select any history or
activity row to open the same file details modal as Media Library; files without a
Media Library record show their name, size, and cache coverage only.

### Parallel lanes and connection budget [since unreleased](https://github.com/johoja12/infinidysk/issues/148){ .nzbdav-since }

Warming does not need bytes in playback order. While nothing is playing, a job fills
its missing range out of order in up to four **lanes**: each lane has its own Usenet
pipeline and takes the next unfinished cache chunk, so one slow or retried article
holds back only its own chunk instead of the whole job. A job uses at most half the
native-cache buffer slots (`cache.native.writer-mb` ÷ 4) for lanes. Extra lanes pause
while anything is playing or fewer than half the slots are free, and rejoin about a
second after that clears; the first lane always keeps going. Near the end of the range,
the remaining work is split into smaller pieces (down to 8 MiB) so every lane stays busy
until the job finishes. All lanes share the job's single cache-space reservation, which
grows chunk by chunk under the same quota and free-space checks.

Each job's connection budget follows free provider capacity [since unreleased](https://github.com/johoja12/infinidysk/issues/147){ .nzbdav-since }.
About once a second it is recalculated as the connections the job already holds plus
free transfer capacity, minus a reserve of a quarter of all transfer slots, up to 24.
Whenever any provider has transfers waiting (playback or imports queued), the budget
drops to **Minimum connections per job** (formerly *Connections per job*). Shrinking
never interrupts a transfer; the job simply stops taking new connections until it is
back under budget. Before this release the setting had no effect, because warming
shared the import queue's connection budget.

### Live speed and sources [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since }

A running job shows its current speed (the last 20 seconds), its average so far, and an
estimate of the time left, for example `18.4 MB/s now · 14.2 MB/s avg · ETA 4m 21s`.
Range jobs estimate from the rest of their own range. A job that has reported no
progress (fetching or verifying) for a minute reads **Stalled**, with how long it has
been quiet.

Each row names its concrete sources as colour-coded bubbles:

- the Plex hub or collection title, for example *Popular TV This Year*;
- **Playing now** (verified Plex playback) and **Watch history**;
- **Next episode · realtime** and **Next episode · history** (predictions);
- **Playback not yet cached** (backfill), **Finish partially watched**, **Manual**,
  and **Read activity**.

A hub or collection removed from the settings shows as *Selected Plex
hub/collection*. A job requested by several sources shows two bubbles and `+N`; hover
over `+N` to see the rest.

### Prediction viewers [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since }

Prediction bubbles include the Plex user whose activity triggered the prediction, for example **Next episode · history · Alice**. When multiple viewers contribute to the same job, each resolved viewer has a separate source bubble. Long names wrap and remain available in the bubble tooltip. If the owning user's name cannot be resolved, the bubble shows **Unknown user**.

### Range jobs and backfill [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since }

Some jobs warm only part of a file: **Playback not yet cached** (backfill),
minimum head/tail ranges, and resume ranges. Their rows show how much of *their own
range* is cached for the file's current revision, for example
`Cached the 4 MiB playback missed ✓` or `12 MiB of 40 MiB of the missed range cached`,
with whole-file coverage as secondary text. A finished backfill of a 7 GB file
therefore reads as done, not as "1% cached". A queued backfill reads
`Waiting to cache the 8 MiB playback missed`. When the current revision cannot be
resolved (for example after the source changed), the row says the range coverage is
unavailable.

Backfill fills in blocks that playback streamed without caching them, for example
when a commit failed or a buffer or probe timed out. Requests for one file are held
until playback of that file has been quiet for about 45 seconds (at most 3 minutes).
Ranges within 64 MiB of each other are then merged, keeping at most eight ranges per
file. A merged range joins queued work for the file, or reopens a backfill-only job of
the same cache revision that completed in the last 6 hours, instead of adding a row.
One playback session therefore produces a handful of rows rather than one per 4 MiB
block. Warming skips blocks that are already cached, so a merged range only fetches
what is missing.

### Playback gap fills [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since }

**Warming history** has two tabs so a small backfill never looks like a cached file:

- **Prefetch warming** lists policy, manual, and finish-watched jobs with whole-file
  coverage. Filter by **Fully cached**, **Partially cached**, **Failed**,
  **Cancelled**, or **Expired**.
- **Playback gap fills** lists backfill jobs. Each row says where the gap was, for
  example `Playback streamed 4 MiB at ~21:25 of 52:00 directly from Usenet.`, and
  draws the gap on a map of the whole file. Whole-file coverage is a footnote
  (`File overall: 3% cached`). Running gap fills read `— filling it in now`. Gap fills
  waiting in the **Activity** tab use the same layout.

Each gap fill names why playback did not cache those bytes. Filter the tab by reason:

| Reason | What happened |
|--------|---------------|
| Cache buffers full | Every cache buffer was busy, so playback read the block straight from Usenet. |
| Block already filling | Another reader was caching the same block, and playback did not wait. |
| Cache storage too slow | A cache read or write missed its deadline. |
| Cache write queue full | Too many cache writes were pending. |
| Cache write failed | Writing to cache storage failed or was rejected. |
| Source interrupted | Usenet fetching hiccuped, so caching paused for the rest of that stream. |
| Source changed | The file was replaced during playback. |
| Read without caching | Playback read the bytes directly without a more specific cause. |
| Reason not recorded | The gap fill was recorded before this release. |

When Plex reports the session (realtime playback checks enabled and the file mapped),
the row also names the player and user, for example `Living Room TV · alex`, and
estimates the playback time from the byte offset and the Plex runtime. Without a Plex
session the row shows the position as a percentage of the file instead. Gap fills
that merge or reopen keep the most recent reason and viewer.

**What stays uncached.** Native Cache keeps what was played, backfill of anything
playback missed, and the files your prefetch policies select (Plex hubs and
collections, playback and history predictions, manual requests). The unplayed rest of
a partially watched file is fetched from Usenet when it is played, and cached then,
unless you turn on [finishing partially watched files](#finish-partially-watched-files).

### Failure reasons [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since }

Failed and deferred jobs show a headline from a stable reason code. The full message
is under **Details**.

| Headline | Meaning | What happens |
| --- | --- | --- |
| Release damaged on Usenet | Articles are missing, corrupt, or belong to a different post on every provider. This is also reported when the same block fails verification on every retry. | The job fails immediately and the file goes to the normal repair path (the same urgent health check that playback failures trigger). The row shows **Queued for repair**, **Repair pending** (the failure threshold under Settings → Health & Repairs is not reached yet), or **Repair disabled**. While repair is pending, the file also moves to the front of the health-check queue; that check includes the articles warming found missing and, if it confirms the damage, repairs the file under the normal repair rules. For 24 hours, or until repair changes the file's revision, policy refreshes, backfill, and finish-watched do not queue the file again. Manual warming and Retry still run. |
| Source bytes did not verify | Source bytes arrived without integrity proof. | Retried with the normal retry limit, then treated as a damaged release. |
| Cache storage error | Verified bytes could not be written to the cache folder named in the message, or no writable folder had room. | Retried. Check that folder's free space, permissions, and mount. |
| Source changed | The file's source was repaired or replaced while it warmed. | Deferred without using a retry. Coverage is rechecked against the new revision. |
| Daily budget reached / Waiting for playback to finish | Budget or playback priority. | Deferred until budget or playback allows. |
| Paused for a more urgent warm | A higher-priority job was waiting and this file's viewers were far enough ahead. | Deferred without using a retry; resumes when a worker is free. |

### Repair outcomes in history [since unreleased](https://github.com/johoja12/infinidysk/issues/200){ .nzbdav-since }

After a damaged file is removed, its warming history uses the recorded repair's
original release title and current outcome. **Replacement requested** means a
search was requested, **Replaced** means the replacement is available through its
verified InfiniDysk library link, and **Replacement fully warmed** means that
replacement's current revision completed whole-file warming. Select the row to
open the replacement's file details when its identity is available.

The original warming attempt still shows **failed**, its original bytes and timing,
and its original error under **Details**. A withheld search, failed or skipped
repair, unavailable replacement, or unconfirmed outcome is shown separately;
deleting a file does not by itself confirm a successful repair. Older records
without repair provenance may still show **Removed media**.

### Finish partially watched files [since 1.6.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.6.0){ .nzbdav-since } { #finish-partially-watched-files }

**Finish caching partially watched files** (Smart Prefetch → Advanced settings →
Warming) is off by default. When it is on, a foreground read session that plays at
least **Finish a file after this much was played (%)** of a file (default 10%, range
1–90, and at least 64 MiB) over at least two minutes queues whole-file warming of
that file. These jobs show the source **Finish partially watched**. They use the daily
download budget, respect pause-during-playback, and skip releases known to be
damaged. The two-minute minimum keeps fast library scans from counting, but a long
scan that reads a large share of a file, such as intro or credit detection, can still
qualify. Turning the option, or Smart Prefetch, off cancels finish-watched work that
is still queued. Other policy changes keep it.

When application shutdown begins, warming stops taking new jobs and interrupts
active work without treating it as a source failure or spending a retry attempt.
Verified bytes remain cached. After restart, enabled Plex policies rebuild their
work; restored manual requests remain paused until resumed.

A failed job requires **Retry** from the Smart Prefetch activity page. Retry
rechecks existing cached blocks and fills missing ranges; it does not clear the
cache. Jobs that failed on an older release retain their failed state until
explicitly retried. Genuine failures include a safe category and exception type,
with a matching job/item diagnostic in backend logs. Raw exception messages are
omitted because they may contain private paths or provider credentials.

The daily cap accounts provider BODY payload lines, including yEnc framing and
retried payload, rather than just final media bytes. It excludes NNTP status lines,
the terminating dot, and TCP/TLS overhead; it is not an exact network-interface
meter. Small durable credits bound concurrent in-flight warming. A transport batch
can cross the cap before cancellation, but those observed bytes remain charged.
Cache hits spend no provider credit. If accounting storage fails, warming stops
across jobs until local metadata is repaired and the service restarts; ordinary
playback is not charged to this budget and remains available.

Before reporting warming complete, InfiniDysk reads and verifies the existing
cached blocks in the requested range. Missing or damaged blocks are fetched again
under the normal warming budget; unavailable storage defers completion. This also
means a repeated whole-file warm reads the cached file from disk, even when it
requires no Usenet traffic. The [historical regression audit](../testing/native-cache-regression-audit.md)
documents the protections and remaining storage/client-cache limitations.

Disabling a source/trigger removes its ownership of pending work. Another enabled
source or a manual request can retain the same job. Removing its last owner cancels
active work. On restart, manual interrupted jobs restore paused; speculative work is
recomputed from current policy. Retry does not discard already verified coverage.
Overlapping queued ranges coalesce without losing manual/source ownership, original
age, or retry delays; backfill also merges nearby ranges as described above. Running ranges stay immutable and same-item jobs serialize.
Bulk warming returns an accepted, deduplicated, or rejected outcome for each item;
one invalid item does not discard the other accepted requests.

## Setup and rollback

These are advanced, opt-in controls, not new-install prerequisites. Plex source
accordions and their optional `DisabledLibraries` state therefore remain outside the
setup wizard; the field defaults to empty, the setup completion allowlist is unchanged,
and `SetupWizardService.CurrentWizardVersion` does not increase. The same applies to
**Finish caching partially watched files** and its threshold. They are advanced
options inside the existing `smart-prefetch.settings` value, so the setup wizard
does not show them. Rerunning a strategy
preserves an explicit Native mode and does not silently rewrite imported files. Review
restart and environment conflicts before Apply. Keep the test branch separate and
switch back to your prior image and configuration backup if needed; do not delete
production cache folders as rollback.

Before adopting a 50 TB deployment, run the opt-in HDD/NAS canary: cold/warm seeks,
concurrent playback plus warming, full/readonly/offline folders, mount replacement,
interrupted writes and restart reconciliation, and an extended prediction soak.
Report actual throughput and memory separately from synthetic catalogue scale tests.
See the [isolated test and canary procedure](../testing/native-cache-prefetch.md).

Support packs omit Plex accounts/server mappings, source selections, and native
folder/metadata paths. Do not include unredacted settings or provider/Plex tokens
when sharing a test report.
Aggregate native hit/miss/commit/fallback/timeout counters are available in the UI,
support pack, and existing Prometheus endpoint. Memory snapshots include configured
native buffer budget and reserved admission, including still-running timed-out IO;
these are not measurements of the NAS page cache.

## Cache reliability follow-ups [since unreleased testing branch](https://github.com/johoja12/infinidysk/issues/38){ .nzbdav-since }

Partial warming can restart on another eligible folder when an unpinned entry's
original folder is full or offline. This refetches verified source bytes through
the warming budget; it does not stripe a file across folders. Previous allocation
remains charged until safe cleanup. Pinned/read-only entries stay in place.

The buffer budget reserves one block for overflow playback reads when at least
8 MiB is configured. Playback writes can finish after the current read returns;
only durably published blocks count as coverage. Status checks time out with an
explicit unknown state. Independent filesystems have separate writer gates;
folders on the same filesystem share space accounting and serialization.

Raw read hints require 64 MiB of contiguous reads over at least 30 seconds. A seek
or a gap over 15 seconds resets eligibility. This filters probes and previews but
cannot identify every scanner. Keep the raw-read trigger off for Plex-only warming.

After a source repair, native-backed WebDAV files expose a changed modification
time. Refresh metadata on each rclone mount and reopen the file to revalidate its
payload. Already-open player buffers remain outside the server's control.
Routine warming of a file that is already cached only re-reads a sample of its
blocks (the first four, the last two, and 2% of the rest, from 16 up to 128 blocks of
4 MiB), and a policy refresh skips the file entirely while it was verified within
**Intent lifetime**. Manual warms still hash every block.
[Since unreleased](https://github.com/johoja12/infinidysk/issues/45){ .nzbdav-since },
a file whose cached blocks are unchanged since its last verification is skipped for up
to seven days even after Intent lifetime passes, so unchanged files are rechecked about
weekly; refilled, rewritten or partly evicted files warm again as usual. Routine
rechecks read at most 64 GiB of cached data per UTC day; a job that reaches the limit
waits until the next day instead of completing unverified. Playback still hashes every
block it serves. Only concurrent in-flight verification is shared. See the [validation report](../testing/native-cache-followups.md)
for exact boundaries and upgrade/downgrade notes. Back up `/config` before upgrading.

## Loading Smart Prefetch pages [since unreleased](https://github.com/johoja12/infinidysk/issues/187){ .nzbdav-since }

The activity page loads queue status independently of current range coverage. Range coverage is checked for the visible page afterward; pending and unavailable checks are labeled explicitly. History filters for fully or partially cached jobs check all relevant retained range jobs before their coverage can determine a match. A slow refresh does not start overlapping status polls, and existing status remains visible during refresh. Coverage checks finish independently of queue updates, so a slow historical check can complete while activity keeps refreshing.

The Plex source picker shows libraries and available sources as each request finishes. A slow library or user lookup no longer delays every other library's sources. Failed source refreshes preserve the previous catalog and your saved selections.
