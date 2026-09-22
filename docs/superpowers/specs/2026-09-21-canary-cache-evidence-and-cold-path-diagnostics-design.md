# Canary Cache Evidence and Cold-Path Diagnostics Design

## Goal

Measure six additional canary files without deleting caches or restarting services,
separate direct-backend performance from rclone-mounted performance, correct the
benchmark's misleading cache labels, and collect enough bounded telemetry to locate
the cold-streaming bottleneck.

## Constraints

- Keep `/mnt/plex2` isolated and unregistered with Plex.
- Do not mutate Plex, Arr, the production `/mnt/plex` tree, imported releases, or
  existing canary links.
- Do not delete cache entries or restart InfiniDysk, NzbDav, or either rclone mount.
- Retain the original six-file reports and create new evidence beside them.
- Use direct backend ports `8081` for legacy NzbDav and `8080` for InfiniDysk.
- Bound every read by byte count and elapsed time.

## Root Cause

`CanaryCacheEvidence` currently calls a cache entry `observed-warm` when the rclone
VFS data file exists and its logical length equals the media length. Rclone creates
sparse files with the final logical length before all ranges are resident. The six
measured InfiniDysk cache files had only 0–21 percent physically allocated despite
being labeled warm. Their sibling `vfsMeta` JSON files contain `Rs` entries that
authoritatively describe cached byte ranges.

Logical length and allocated blocks are therefore insufficient. The benchmark must
normalize and union `vfsMeta.Rs` ranges, clamp them to the expected file size, and
derive cache state from actual covered bytes.

## Cache Evidence Model

Replace the nullable string classifier result with a value containing:

- label: `observed-cold`, `observed-partial`, `observed-warm`, or
  `cache-state-unknown-*` when evidence is unavailable or invalid;
- cached bytes after normalized range union;
- expected bytes;
- coverage percentage;
- evidence source (`rclone-vfs-meta` or `none`).

Classification rules:

1. Missing data and metadata entries are `observed-cold` with zero cached bytes.
2. Valid metadata with zero covered bytes is `observed-cold`.
3. Valid metadata covering less than the expected size is `observed-partial`.
4. Only continuous normalized coverage of the full expected file is
   `observed-warm`.
5. Missing, malformed, out-of-bounds, or inconsistent metadata is unknown rather
   than optimistically warm.

The JSON and Markdown reports will include cached bytes and coverage percentage.
Existing route, timeout, error, and diagnostic-only fields remain unchanged.

## Six-File Diagnostic Selection

Choose exactly six links from the existing 30-link plan that were not in the first
benchmark. Include movie and TV paths, HD and 4K resolutions, direct and
RAR/multipart representations, and exactly one large-file case. All 30 files were
partially touched by validation, so the new selection is not described as wholly
unread. Instead, capture each file's `vfsMeta` ranges before testing and choose
bounded windows outside those ranges.

Preserve the selection, pre-test range map, commands, raw timing output, and a
Markdown summary in the existing root-only evidence directory. Run the existing
benchmark matrix with its fixed 8 MiB seeks, 128 MiB sequential window, and a
180-second per-operation timeout. Describe its first pass as partially cached when
the range evidence requires that label.

## Direct-versus-Mount Comparison

After the standard matrix, capture a second range map. For each selected file,
choose two different uncached 32 MiB windows:

- a direct WebDAV read with `rclone cat REMOTE:PATH --offset OFFSET --count
  33554432` inside the owning rclone container, which reaches the backend port
  without the VFS mount cache; and
- a read through the corresponding host rclone mount.

Use a 120-second timeout. Alternate which path runs first across files and alternate
which relative window is assigned to each path to reduce ordering and offset bias.
Record route, offset, requested and actual bytes, time to first byte, completion
time, throughput, timeout, and errors. Re-read each path's window once to show warmed
behavior. A window is invalid if the second range map already covered it.

## Telemetry and Bottleneck Attribution

Capture before/after snapshots without resetting counters:

- rclone `core/stats` for both mounts;
- InfiniDysk health, revision, restart count, and focused warning/error counts;
- focused backend streaming/read diagnostics available from the running container,
  without printing credentials or configuration;
- rclone log excerpts limited to the diagnostic window;
- cache range maps before and after each file.

Attribute the bottleneck by boundary:

- direct backend slow and mount slow: backend/NNTP/archive cold path;
- direct backend fast and mount slow: rclone VFS/cache behavior;
- both fast after the first bounded read: expected cache-fill behavior;
- representation-specific slowdown: direct versus multipart source path.

Do not change concurrency, provider, cache, or timeout configuration during this
diagnostic run.

## Testing

Use test-driven development for the classifier and report changes:

- sparse logical-size file with partial `Rs` coverage is partial, not warm;
- overlapping/adjacent ranges are normalized without double counting;
- complete coverage is warm;
- missing entries are cold;
- malformed, negative, overflowing, out-of-bounds, or size-inconsistent metadata is
  unknown;
- JSON and Markdown include evidence source, cached bytes, and coverage percentage;
- existing performance probe and route behavior remain unchanged.

Run the focused canary-performance tests locally. PR CI remains authoritative for
the broader application suite under repository policy.

## Delivery

Commit the focused fix and documentation on `fix/canary-cache-evidence`, push it to
`johoja12/infinidysk`, and open a PR to `main`. Do not merge or enable auto-merge.
The production diagnostic evidence is retained separately on nuc-1 and is not
committed to Git.
