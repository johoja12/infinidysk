# Awaiting-import RCA — 2026-10-13

## Finding

The nine visible releases in the reported Overview screenshot have several distinct
blockers. Five have invalid media bytes in the posted source; three have Arr
grab-context identity disagreements; one is a multipart movie Radarr rejects.
InfiniDysk is not introducing the zero-filled opening bytes in the four examined
zero-header files. It does have a pre-completion validation gap for archive outputs.

The earlier hypothesis that cache corruption or file assembly might explain the
zero-filled headers is superseded by direct source evidence below.

## Scope and observations

Read-only queue, manual-import, parse, and log requests were made to the configured
Radarr, Sonarr, and Sonarr4K instances. Direct WebDAV reads, mount reads, persisted
multipart metadata inspection, and bounded NNTP BODY reads were compared. Credentials
were consumed inside protected commands and were not recorded in this report.

The initial application image was `local/infinidysk:98515376`. A separate operation
changed it to `local/infinidysk:32050377`, started at 09:55:47 UTC. The final 16 KiB
source-versus-WebDAV comparisons reproduced the findings after that change. This
investigation did not deploy or restart services.

No queue items, library links, source blobs, settings, or cache entries were changed.
The Article 370 header experiment changed only an isolated diagnostic prefix copy.

| Release | Confirmed immediate cause |
| --- | --- |
| Shotgun Wedding | Correct first RAR volume contains zero-filled media payload; ffprobe cannot read an MKV header. |
| Girls5eva S02E03 | Correct first RAR volume contains zero-filled media payload. |
| Ozark S02E01 | Correct first RAR volume contains zero-filled media payload. |
| You S03E03 | Correct first RAR volume contains zero-filled media payload. |
| Article 370 | Source MKV declares `EBMLMaxIDLength=20`; the unmodified source prefix fails ffprobe. |
| The Best of Youth | Two actual movie parts are exposed; Radarr rejects both as unsupported multipart files. |
| Little | Movie parsing succeeds, but Radarr rejects it when the tracked download/grab context is supplied. |
| Lab Rats S02E14 | Sonarr maps the file to S02E14 and S02E15, both with scene episode 14; tracked grab-context validation rejects it. |
| Lab Rats S04E01 | Sonarr maps the file to S04E01 and S04E02, both with scene episode 1; tracked grab-context validation rejects episode 4x02. |

## Source evidence for the five media-probe failures

The persisted files are unencrypted multipart output. Each inspected first RAR
article has valid yEnc CRC and RAR-header CRC. The first archive volume is volume 0;
its member is stored, not compressed, is not split-before, and is split-after. The
RAR member-data offset exactly matches InfiniDysk's recorded first-part offset.
These observations rule out serving a continuation volume first or confusing
compressed archive data with media for the examined opening bytes.

| Release | Recorded/header data offset | First-article media payload examined | Source result |
| --- | ---: | ---: | --- |
| Shotgun Wedding | 131 | 767,869 bytes | Entire examined media payload is zero. |
| Girls5eva | 207 | 716,593 bytes | Entire examined media payload is zero. |
| Ozark | 189 | 716,611 bytes | Entire examined media payload is zero. |
| You | 211 | 716,589 bytes | Entire examined media payload is zero. |
| Article 370 | 131 | 767,869 bytes | MKV signature present; malformed EBML length declaration. |

Shotgun Wedding, Girls5eva, You, and Article 370 were retrieved from both
`reader.xsnews.nl` and `news.newshosting.com`. The decoded article SHA-256 values
match across the two providers. Ozark returned NNTP 430 on XSNews and a CRC-valid
article with zero-filled media data on Newshosting.

All five first 16 KiB media prefixes served through direct InfiniDysk WebDAV match
the source bytes at the recorded member offset exactly. For the four zero-header
files, both source and served prefix SHA-256 are:

```text
4fe7b59af6de3b665b67788cc2f99892ab827efae3a467342b3bb4e3bc8e5bfe
```

Article 370 source and served prefix SHA-256 are:

```text
7acf902fafe7c0f04110c05dc9f0126a95cc6ebbd4a20757b6d89f0ab0428663
```

The examined prefixes are therefore invalid in the posted payload, not corrupted
by InfiniDysk's cache or the rclone mount. Passing article CRC validates transport
against the posted article; it does not establish that its media content is valid.
The tests do not establish when or why the upload became invalid, full-file
integrity, or whether PAR2 recovery can restore any of these releases.

### Article 370 diagnostic control

The source begins with an MKV/EBML signature. At media byte offset 13, the element
encoding `42 f2 81 14` declares a maximum ID length of 20. Both providers supply
the same bytes. ffprobe on the extracted source prefix exits 1 with
`Not yet implemented in FFmpeg, patches welcome`; the mounted file fails likewise.

Changing only that element value from 20 to 4 in an isolated prefix copy makes
ffprobe exit 0 and report H.264 at 1920×1080, E-AC-3 audio, SubRip subtitles, and
9,390.834 seconds duration. This confirms the immediate header blocker without
claiming that the complete film is healthy. The live blob and cached content were
not patched.

## Why InfiniDysk reports completion

There are two code-backed gaps in the existing readiness path:

1. `FinalMediaReadinessValidator.PlanTargets` calls
   `FileAggregator.PlanDirectFiles`, which accepts only `FileProcessor.Result`.
   RAR, 7z, and split-media outputs are excluded. A focused probe against the
   initially deployed assembly confirmed that a RAR processor result produces
   zero direct-readiness targets.
2. `ConfigManager.GetMediaReadinessCategories` defaults to `tv`, `movies`,
   `movie`, `series`, and `audio` when importable-media checking is enabled.
   The affected download categories are `radarr` and `sonarr4k`; these require
   explicit article-existence categories to enter the readiness stage. The live
   database has no persisted override for the existence-category setting or
   importable-video setting, and the inspected application environment has no
   corresponding override. Consequently, these category names are outside the
   default readiness gate.

Adding categories alone would still leave the archive-output target gap.

`RarAggregator.ValidateVolumes` checks consistency of declared member size and the
sum of member-part lengths. Correct lengths and valid RAR headers can coexist with
zero-filled or malformed media payload. `ImportArticleContentValidator` checks
article identity rather than structural validity of decoded media. None of those
checks substitute for validating the final archive member's opening bytes.

Even the current signature sniff recognizes Article 370 as `.mkv`; detecting that
case needs EBML structural validation beyond the magic signature.

Arr subsequently runs ffprobe to obtain runtime. The zero-filled headers or
malformed EBML declaration prevent probing, so its sample decision is inconclusive.
That produces the misleading `Unable to determine if file is a sample` queue
message; it does not mean that these are confirmed samples.

The live `arr.instances` configuration has an empty `QueueRules` list. The monitor
therefore leaves these warnings in place, explaining the multi-day waits.

## The other import blockers

For Little, Radarr's parse endpoint resolves the space-separated release title,
the dotted title, and the mounted filename to movie ID 2932, Little (2019).
Manual-import inspection with the queue's `downloadId` returns the reported
movie-not-found-in-grab rejection. Repeating the same read-only inspection without
`downloadId` returns the same movie and no rejection. This localizes the blocker
to Radarr's tracked grab-context validation rather than basic filename parsing.
The exact internal Radarr state that causes that disagreement remains unverified.
An existing Little library file was also observed; import must preserve it until
the intended replacement is reviewed.

Both Lab Rats inspections likewise reject only when `downloadId` is supplied.
Without that context, Sonarr recognizes the two-episode mappings with no
rejections. This supports a scene-numbering/expected-episode disagreement. It
does not prove both episodes are present in the video; that requires content review.

The Best of Youth exposes actual `Part1` and `Part.2` files. Both receive Radarr's
explicit multipart-file rejection. Renaming away the part numbers would conceal
the split rather than resolve the movie's two-file shape.

## Remediation and acceptance criteria

This RCA proposes changes; it does not implement or authorize live queue removal,
replacement searches, source repair, forced imports, or deployment.

- Extend pre-completion readiness to the final media output of direct, RAR, 7z,
  split-media, nested, and encrypted paths. Validate after archive slicing and
  decryption, before SAB reports completion, outside the finalization lock. Preserve
  filtering, secret masking, cancellation, bounded reads, and connection budgets.
- Apply readiness to configured media categories, including custom Arr category
  names, rather than relying only on the current hardcoded names. Review setup-wizard
  impact if this introduces a user-facing setting or changes an existing one.
- Reject zero-filled media heads and invalid EBML structure with a concise failed
  download reason. Contain expected validation errors as human-readable warnings;
  keep diagnostic stacks at Debug. Distinguish invalid source bytes from provider
  unavailability so transient failures do not permanently reject a healthy release.
- For the four zero-header releases, review PAR2 feasibility or select replacement
  releases. Cache eviction/restart alone cannot restore source bytes that match the
  cached zeros. Do not delete valid existing library mappings during recovery.
- For Article 370, review replacement or a narrowly audited header repair with
  backups, changed content identity, cache invalidation, and full-file checks. Do
  not silently patch live data based solely on the prefix experiment.
- Review Little's intended movie and existing file, and Lab Rats' complete episode
  content, before a manual import with the correct identities. Review a single-file
  release or an explicit library strategy for The Best of Youth.

Acceptance requires regression cases for zero-filled stored-RAR media, malformed
EBML despite valid magic, valid archived media, custom Arr categories, and transient
source failure. Invalid media must become failed history before Arr can see a
completed item. Valid media must preserve bounded streaming behavior. Any approved
live recovery must separately verify complete media integrity, Arr import/history,
correct episode or movie identity, and preserved existing library links.

## Evidence retention

Private diagnostic artifacts are under `/tmp/stuck-import-rca-20261013` on the
investigation workstation and nuc-1. They include copied metadata blobs,
`metadata.jsonl`, source article samples, `source-evidence.jsonl`, header parsing,
and the isolated Article 370 prefix experiment. These are temporary artifacts,
not a durable backup. No media bytes, NZBs, credentials, or secret-bearing
configuration are included in this documentation commit.
