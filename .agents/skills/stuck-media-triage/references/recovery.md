# Automatic recovery and retry

## Guard the operation, not the whole investigation

An explicit `$stuck-media-triage` invocation authorizes automatic evidence-backed remediation of the Arr/Preview/ID media failures in the requested scope. A bare invocation covers the enabled Arr instances, failed Preview jobs, and correlated current ID sources found during that run. Honor an explicit inspection-only/no-changes instruction. Implicit skill selection follows the enclosing request’s authority. Do not ask the user to approve routine actions already covered by this mode. This scope excludes unrelated quality-profile changes, arbitrary queue deletion, global preview reset, merge, deployment, or sending messages elsewhere.

Refresh evidence immediately before each mutation. Capture a private manifest with exact instance/media/download/file/source/preview IDs, reason, expected preconditions, planned action and verification. Back up relevant state: consistent SQLite snapshots, a verified PostgreSQL dump when used, affected Arr metadata and link targets. Do not copy live WAL databases as if they were consistent backups.

Write and fsync intent **before** the request, then record acceptance and independent verification. Do not automatically retry a timed-out POST/DELETE via a different connector, host or client. Reconcile queue, command, history, filesystem and persisted intent first. Deduplicate active searches and prior accepted commands.

## Execute the remediation pass

After collection and diagnosis, create a private manifest of exact identities, evidence and permitted actions; validate it yourself and start the supported actions. A recommendations-only handoff is incomplete in automatic mode when these preconditions hold:

| Evidence / preconditions | Automatic action | Acceptance evidence |
|---|---|---|
| Stale duplicate queue row; current library file and source verified healthy; removal semantics preserve its target | Remove only the stale queue row without blocklisting or searching | Queue row absent; retained file IDs/targets unchanged and readable |
| Current Preview parts reconciled to authoritative Plex data; relevant regions readable; no known unresolved source damage; no active worker owns row | Refresh narrow stale metadata, then authenticated per-item retry; admit only this batch if priority-only | Worker executes; current part BIF exists and item completes |
| Correct readable media, unambiguous canonical IDs and all shared mappings reconciled | Apply supported existing-file/manual-import context repair | Every intended episode/movie maps to the verified file; companions preserved |
| Current source damage confirmed; exact library and shared-file scope known | Reconcile active ID repair first; use supported scoped repair if available; if unrecoverable, quarantine affected link/metadata, remove only bad registration, reject exact bad release and search affected IDs | Replacement imports under correct IDs, passes relevant media checks, and dependent preview completes |
| Proven ID code defect with reproduction | Implement and validate a focused fix and open the repo-required PR | Reproduction and relevant checks pass; report deployment still pending unless separately authorized |

Do not force-import a different series or treat an empty folder as proof of source corruption. Keep those items unresolved while completing independent supported actions. If a requested operation has no understood per-item API or its preservation semantics cannot be verified, record the concrete blocker instead of guessing. A failed prerequisite is a review-required disposition, not a reason to request approval for the rest of the batch.

## Failed-preview RCA, refresh and reset

Run this sequence automatically for each failed item in scope, including failures that recur during the monitor. A failed flag by itself is not the root cause.

1. Archive the original failure, attempt time, progress/failing offset, current row and BIF metadata in the run journal. Record the exact rating key, all current parts and ID UUIDs, Arr ownership, and current worker ownership. Read the latest **per-source ID health and repair history for every candidate before reset**, including previously failed ownership checks and still-running repairs. Historical prefetch failures and stale preview errors alone do not establish current damage.
2. Write a concise RCA stating symptom, old versus current source, discriminating evidence, cause/confidence, corrective action and acceptance check. Separate source/article damage, ID serving defects, stale metadata, mount/transport pressure and worker/GPU errors. If the cause is unresolved, state precisely which evidence is missing and continue independent items.
3. Verify the relevant failing region on the current source. A successful seek/frame after a corrupt region does not clear it: use bounded contiguous decoding through that region or exact range/source checks. FFmpeg can return zero and produce frames while reporting EBML corruption; inspect its error output as well. A consumer timeout is not proof of a bad replacement: compare consumer and direct-ID/source evidence and distinguish slow decoding from blocked transport before choosing another regrab.
4. Correct the cause. Repair/replace confirmed damaged sources and verify their resulting imports first; retain a held-for-source-repair disposition while that work is pending. For readable current media, refresh its authoritative Plex metadata and normal application sync. If sync demonstrably leaves cached parts stale, back up state and conditionally reconcile only the reviewed row from authoritative parts, including primary path, every version/part, bundle hashes and BIF paths. Never invent a hash or mutate a row owned by a queued/processing worker.
5. Once the current source is verified readable at the relevant region and no known unresolved damage remains, **reset that item's failed state and regenerate automatically** through the authenticated per-item API. Inspect the installed endpoint's actual reset semantics. Verify the intended queued/missing transition and reset progress; stale error/worker telemetry must clear through the supported reset or dispatch lifecycle. If the API lacks a needed metadata correction, use only a backed-up conditional repair while the row is unowned, before queue admission. Preserve historical errors in the journal, durable retry counters, source-damage evidence and existing valid BIF backups. Never globally reset failures or mark work completed to clear an error.
6. Verify admission, advancing worker progress and a valid BIF for each current part. Record reset acceptance separately from dispatch and completed generation. If the retry fails, collect fresh evidence and repeat RCA; reset again only after a demonstrated cause correction/new verified source and within the existing attempt budget. Otherwise leave a concrete review-required outcome. Distinguish a deliberately held retry from one that actually executed and failed.

The action summary must include the RCA and outcome for each affected item: refreshed/reset and verified complete, queued/processing, held awaiting source repair, or review-required with its concrete reason. A reset must resolve stale state rather than erase evidence or conceal an ongoing source failure.

## Choose the smallest repair

- **Existing healthy library file:** verify exact file ID, path, target and media. Clear only the stale release, `blocklist=false`, with no new search; verify the library identity remains intact.
- **Verified bad/incompatible release with no good library file:** reject the exact bad release and request a scoped replacement. A failed search, queued job, or completed download is not proof of repair.
- **Bad source already registered in the library:** confirm the current file still points to the reviewed bad ID; inspect all episodes sharing the file. Preserve the exact link and metadata in quarantine before removing that registration. Do not overwrite/delete unrelated or uncertain files. Search only affected media IDs.
- **Readable media with wrong grab/scene context:** explicitly adopt the verified file for correct canonical IDs. Library-local symlinks to stable `.ids` sources plus an existing-file manual import can avoid copying gigabytes. Check the installed Arr version's `ManualImport` semantics: a library-local path without `downloadId` is often registered as an existing file. Preserve all previous file IDs/paths and paired-episode mappings; never blindly force an imported companion.
- **Preview retry:** verify current parts/versions and source readability; back up existing BIF/state and use the authenticated per-item regenerate operation. It can delete an existing BIF. Do not fabricate completion, forge login cookies, globally reset failed jobs, or restart an unrelated active worker.
- **Stale Preview metadata:** refresh the narrow Plex library/series path and application metadata. If normal sync demonstrably leaves the reviewed parts/hashes stale, a backed-up, conditional repair of those exact rows from authoritative Plex data may be appropriate within authorized recovery. Never change a row already owned by a queued/processing worker. Reconcile primary path, all parts, bundle hashes and BIF paths together.

Arr deletion for a verified stale record commonly uses:

```text
DELETE /api/v3/queue/{verifiedQueueId}?removeFromClient=true&blocklist=false&skipRedownload=true
```

Change `blocklist` only for a genuinely bad/incompatible release, with session intent preserved. Validate semantics on the installed version and completed-file deletion settings; do not assume removing history preserves linked virtual content. After removal, check that every retained `.ids` target remains readable.

## Finite recovery automation

Use a reviewed manifest, locks, durable journal, status artifact, per-target attempt bound and deadline. Unless the user specifies tighter limits, use one initial Preview retry and at most one additional retry after new evidence of repair/source change, at most three replacement attempts per target, and a deadline 24 hours after the run starts. Record tighter bounds when appropriate. Read prior journals and active commands first: rerunning the skill must not reset an exhausted attempt budget for the same unresolved source. A verified new source is a new identity, not permission to repeat an unchanged failing request. Do not leave a job merely queued when scoped admission can be performed safely; if work outlasts the turn, leave one task-owned monitor with its persisted manifest, lock, journal, deadline and stop conditions.

- Persist source identity and evidence per attempt. Refuse a changed/shared file unless its new scope is reconciled.
- Verify replacement files beyond the header when the original defect was at a later offset. Confirm movie runtime/completeness, episode IDs/plots, language/quality and all intended media parts. Normalizing a parser-hostile release name does not fix its source bytes.
- Review completed-but-blocked results and repeated incompatible selections. Reconcile prior accepted searches before choosing another source. Do not cycle forever across indexers serving the same post.
- For priority-only Preview scheduling, test **admission and execution**. If scoped temporary admission is needed, change only the reviewed batch, retain original priority metadata and restore it on completion without overwriting a newer automatic priority decision. Do not enable the global backlog or interrupt active playback.
- A monitor must actually be running, with new progress/status snapshots. Persist its command and manifest so it can be resumed after interruption. Restart only the task-owned monitor; stopping a host Docker CLI may leave the container exec process alive. Reconcile the lock-owning process before starting another copy.
- Stop on uncertain writes, unexpected source/file identity, preservation failure, shared-file ambiguity, auth failure, bounded repeated failure, or deadline. Record a review-required disposition rather than quietly abandoning the item or claiming success.

Keep retained working alternatives separate from known bad sources. Do not replace a full movie with an imported half, a missing episode with the wrong plot, or a failed preview with a flag that merely says completed.

## Verify and hand off

Separate these evidence layers:

1. Source selected and search/submit accepted.
2. ID download completed or rejected with a specific reason.
3. Arr library file registered with the intended ID/path/source and companion mappings.
4. Media readability at the required failing region and plausible full duration.
5. Preview retry admitted and dispatched.
6. Final BIF exists for the current source/part and Preview reports completion.

Check old scoped queue IDs are absent, retained library IDs/targets are unchanged, and unrelated worker/Prefetch states are preserved. Report pending work, limits and the monitor's status path explicitly. Repository changes follow `AGENTS.md`; use `chore(skills)` for this skill and non-behavior changes. Follow the available guarded deployment workflow only when a product fix and deployment are authorized.
