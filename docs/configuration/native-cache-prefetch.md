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
coverage and a local indexed catalogue. Completing a partially cached movie fills
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

Use **Settings → Streaming → Native cache**. Select Off, Segment, or Native.
Settings show configured and active modes separately until a restart activates the
change. A Native failure falls back to ordinary source streaming, never to Segment.
Explicit mode changes retain inactive cache files for rollback.

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

| Configuration key                | Purpose                                                             |
| -------------------------------- | ------------------------------------------------------------------- |
| `cache.mode`                     | `off`, `segment`, or `native`                                       |
| `cache.native.folders`           | Bounded JSON array of native folder settings                        |
| `cache.native.metadata-path`     | Direct local path for rebuildable catalogue and warming state       |
| `cache.native.writer-mb`         | Native buffering allowance in MiB; 32 by default                    |
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
selection is available under **Plex libraries and sources**, and queue inspection,
policy preview, manual warming, and retry controls are under **Prefetch activity**.
Both sections stay collapsed until needed so the everyday setup remains limited to
Enable, Movies, TV episodes, and the daily GB budget.

Choose a saved server and watching profile, then expand **Movies** or **TV**. Switch
on a library to use its recommended sources: movie libraries select **Recently
Added**, while TV libraries select **On Deck** and **Continue Watching** when Plex
provides them. These defaults apply only the first time a library has no existing
source choices. Expand **Collections** only when needed; collections remain off by
default. Use **Customize** for a source's item limit, excluded TV show IDs, and
preview. Finish with **Apply source changes**, which saves only Smart Prefetch and
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
the prediction.

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
foreground admission. Progress represents verified committed whole-file coverage,
not bytes merely read from providers or the size of one requested range.

The daily cap accounts provider BODY payload lines, including yEnc framing and
retried payload, rather than just final media bytes. It excludes NNTP status lines,
the terminating dot, and TCP/TLS overhead; it is not an exact network-interface
meter. Small durable credits bound concurrent in-flight warming. A transport batch
can cross the cap before cancellation, but those observed bytes remain charged.
Cache hits spend no provider credit. If accounting storage fails, warming stops
across jobs until local metadata is repaired and the service restarts; ordinary
playback is not charged to this budget and remains available.

Disabling a source/trigger removes its ownership of pending work. Another enabled
source or a manual request can retain the same job. Removing its last owner cancels
active work. On restart, manual interrupted jobs restore paused; speculative work is
recomputed from current policy. Retry does not discard already verified coverage.
Overlapping queued ranges coalesce without losing manual/source ownership, original
age, or retry delays. Running ranges stay immutable and same-item jobs serialize.
Bulk warming returns an accepted, deduplicated, or rejected outcome for each item;
one invalid item does not discard the other accepted requests.

## Setup and rollback

These are advanced, opt-in controls, not new-install prerequisites. Plex source
accordions and their optional `DisabledLibraries` state therefore remain outside the
setup wizard; the field defaults to empty, the setup completion allowlist is unchanged,
and `SetupWizardService.CurrentWizardVersion` does not increase. Rerunning a strategy
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
