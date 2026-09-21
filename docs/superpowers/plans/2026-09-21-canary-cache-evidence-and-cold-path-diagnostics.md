# Canary Cache Evidence and Cold-Path Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct rclone sparse-cache classification, benchmark six additional canary files, compare direct WebDAV reads with rclone-mounted reads, and identify the cold-streaming bottleneck without clearing caches or changing Plex/Arr.

**Architecture:** Parse the authoritative rclone `vfsMeta` range journal beside each VFS cache file, normalize its byte ranges, and carry structured cache evidence into every benchmark observation and report. Publish the branch CLI to a versioned root-only directory on nuc-1, then run bounded production diagnostics against the existing 30-link `/mnt/plex2` canary while preserving before/after telemetry and checksums.

**Tech Stack:** .NET 10, C#, System.Text.Json, xUnit, rclone WebDAV/VFS, Docker, jq, Python 3 one-shot bounded measurement harness, SHA-256 evidence manifests.

---

### Task 1: Parse authoritative rclone VFS cache ranges

**Files:**
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryCacheEvidenceTests.cs`
- Modify: `tools/NzbDavMigration/Canary/CanaryCacheEvidence.cs`

- [ ] **Step 1: Write the failing cache-evidence tests**

Create `CanaryCacheEvidenceTests.cs` with a disposable temporary layout rooted at
`<temp>/cache/vfs/remote`. The helper must create a library file, mirror its
mount-relative path beneath both `vfs/remote` and `vfsMeta/remote`, and serialize
rclone-compatible metadata with exact `Size` and `Rs` properties.

Cover these exact assertions:

```csharp
[Fact]
public void Classify_UsesNormalizedMetadataRangesInsteadOfSparseLogicalLength()
{
    var fixture = CreateFixture(1_000, [new(0, 100), new(50, 100), new(150, 50)]);
    var result = CanaryCacheEvidence.Classify(fixture.CacheRoot, fixture.LibraryPath, 1_000);

    Assert.NotNull(result);
    Assert.Equal("observed-partial", result.Label);
    Assert.Equal(200, result.CachedBytes);
    Assert.Equal(1_000, result.ExpectedBytes);
    Assert.Equal(20, result.CoveragePercent);
    Assert.Equal("rclone-vfs-meta", result.Source);
}

[Fact]
public void Classify_RequiresCompleteCoverageBeforeCallingCacheWarm()
{
    var fixture = CreateFixture(1_000, [new(0, 500), new(500, 500)]);
    var result = CanaryCacheEvidence.Classify(fixture.CacheRoot, fixture.LibraryPath, 1_000);
    Assert.Equal("observed-warm", result!.Label);
    Assert.Equal(1_000, result.CachedBytes);
    Assert.Equal(100, result.CoveragePercent);
}

[Fact]
public void Classify_ReturnsColdWhenNeitherDataNorMetadataExists()
{
    var fixture = CreateFixture(1_000, [], createCacheFiles: false);
    var result = CanaryCacheEvidence.Classify(fixture.CacheRoot, fixture.LibraryPath, 1_000);
    Assert.Equal("observed-cold", result!.Label);
    Assert.Equal(0, result.CachedBytes);
}

[Theory]
[InlineData("{not-json")]
[InlineData("{\"Size\":999,\"Rs\":[]}")]
[InlineData("{\"Size\":1000,\"Rs\":[{\"Pos\":-1,\"Size\":1}]}")]
[InlineData("{\"Size\":1000,\"Rs\":[{\"Pos\":999,\"Size\":2}]}")]
public void Classify_FailsClosedForInvalidMetadata(string metadata)
{
    var fixture = CreateFixture(1_000, [], rawMetadata: metadata);
    Assert.Null(CanaryCacheEvidence.Classify(fixture.CacheRoot, fixture.LibraryPath, 1_000));
}
```

- [ ] **Step 2: Run the new tests and verify RED**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~CanaryCacheEvidenceTests'
```

Expected: compilation fails because `CanaryCacheEvidenceResult`, `CachedBytes`,
`ExpectedBytes`, `CoveragePercent`, and `Source` do not exist.

- [ ] **Step 3: Implement the minimal range-aware classifier**

Replace the string result with:

```csharp
public sealed record CanaryCacheEvidenceResult(
    string Label,
    long CachedBytes,
    long ExpectedBytes,
    double CoveragePercent,
    string Source);
```

Add private JSON records matching rclone's exact property names:

```csharp
private sealed record RcloneVfsMetadata(long Size, IReadOnlyList<RcloneVfsRange>? Rs);
private sealed record RcloneVfsRange(long Pos, long Size);
```

Implement `Classify` so it:

1. Preserves `null` when no cache root was supplied or the path cannot be safely
   resolved.
2. Requires the cache root shape `<cache>/vfs/<remote>` and derives the sibling
   metadata root as `<cache>/vfsMeta/<remote>`.
3. Returns cold only when both the data and metadata entries are absent.
4. Returns `null` for one-sided, symlinked, malformed, size-mismatched, negative,
   overflowing, or out-of-bounds evidence.
5. Sorts ranges by position and unions overlapping or adjacent entries with checked
   arithmetic.
6. Returns partial for `0 < cachedBytes < expectedSize`, warm only for exact full
   coverage, and cold for a valid empty range list.
7. Calculates `CoveragePercent = cachedBytes * 100d / expectedSize`.

- [ ] **Step 4: Run cache-evidence tests and verify GREEN**

Run the command from Step 2.

Expected: all `CanaryCacheEvidenceTests` pass with zero failures.

- [ ] **Step 5: Commit the classifier**

```bash
git add tools/NzbDavMigration/Canary/CanaryCacheEvidence.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryCacheEvidenceTests.cs
git commit -m "fix(rclone): classify sparse VFS cache ranges"
```

### Task 2: Carry cache coverage into benchmark JSON and Markdown

**Files:**
- Modify: `tools/NzbDavMigration/Canary/CanaryPerformanceModels.cs`
- Modify: `tools/NzbDavMigration/Canary/CanaryPerformanceProbe.cs`
- Modify: `tools/NzbDavMigration/Canary/CanaryBenchmarkRunner.cs`
- Modify: `tools/NzbDavMigration/Canary/CanaryPerformanceReportWriter.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryPerformanceProbeTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryPerformanceReportWriterTests.cs`

- [ ] **Step 1: Write failing propagation and report tests**

Append optional fields to the expected observation contract in the tests and assert
that one partial-cache row preserves:

```csharp
CacheLabel: "observed-partial",
CacheCachedBytes: 200,
CacheExpectedBytes: 1_000,
CacheCoveragePercent: 20,
CacheEvidenceSource: "rclone-vfs-meta"
```

In `CanaryPerformanceProbeTests`, pass a `CanaryCacheEvidenceResult` to
`ProbeAsync` and assert every returned row contains those values. In
`CanaryPerformanceReportWriterTests`, assert the JSON contains
`"cacheCoveragePercent": 20` and the Markdown cache cell contains
`observed-partial (200/1000 bytes, 20%, rclone-vfs-meta)`.

- [ ] **Step 2: Run the focused tests and verify RED**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~CanaryPerformanceProbeTests|FullyQualifiedName~CanaryPerformanceReportWriterTests'
```

Expected: compilation fails because the structured evidence parameters and
observation fields do not exist.

- [ ] **Step 3: Extend the observation model compatibly**

Append these optional fields after `Error` in `CanaryPerformanceObservation` so
existing positional constructors remain source-compatible:

```csharp
long? CacheCachedBytes = null,
long? CacheExpectedBytes = null,
double? CacheCoveragePercent = null,
string CacheEvidenceSource = "none"
```

Change `CanaryPerformanceProbe.ProbeAsync` from `string? provenCacheLabel` to
`CanaryCacheEvidenceResult? cacheEvidence`. Use the evidence label when present;
otherwise retain `cache-state-unknown-first-pass` and
`cache-state-unknown-repeat-pass`. Copy all evidence fields into each observation.

Keep `CanaryBenchmarkRunner` responsible for calling `Classify` immediately before
each side/file/pass probe and passing the structured result unchanged.

- [ ] **Step 4: Render coverage without hiding unknown state**

In `CanaryPerformanceReportWriter`, replace the raw cache label cell with a helper:

```csharp
private static string Cache(CanaryPerformanceObservation row) =>
    row.CacheCachedBytes is long cached
    && row.CacheExpectedBytes is long expected
    && row.CacheCoveragePercent is double percent
        ? $"{row.CacheLabel} ({cached}/{expected} bytes, {percent:0.###}%, {row.CacheEvidenceSource})"
        : row.CacheLabel;
```

Use the first row's formatted cache evidence for each side/pass group. JSON remains
generated from the record and therefore includes the new camelCase fields.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the command from Step 2, followed by:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~CanaryPerformance|FullyQualifiedName~CanaryCacheEvidence'
```

Expected: all cache and canary-performance tests pass with zero failures.

- [ ] **Step 6: Commit model and report propagation**

```bash
git add tools/NzbDavMigration/Canary/CanaryPerformanceModels.cs \
  tools/NzbDavMigration/Canary/CanaryPerformanceProbe.cs \
  tools/NzbDavMigration/Canary/CanaryBenchmarkRunner.cs \
  tools/NzbDavMigration/Canary/CanaryPerformanceReportWriter.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryPerformanceProbeTests.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryPerformanceReportWriterTests.cs
git commit -m "fix(rclone): report exact canary cache coverage"
```

### Task 3: Document the corrected cache semantics

**Files:**
- Modify: `docs/guides/nzbdav-migration.md`

- [ ] **Step 1: Update the benchmark guide**

Replace the statement that cache roots merely label observed evidence with this
contract:

```markdown
When cache roots use rclone's standard `vfs/<remote>` layout, the report reads the
sibling `vfsMeta/<remote>` range journal. `observed-warm` requires complete cached
byte coverage; sparse logical file length is not proof. Partial entries report
cached bytes and percentage. Missing or inconsistent metadata remains unknown.
```

- [ ] **Step 2: Verify documentation and diff hygiene**

```bash
git diff --check
rg -n 'vfsMeta|observed-partial|sparse logical' docs/guides/nzbdav-migration.md
```

Expected: no whitespace errors and all three concepts appear.

- [ ] **Step 3: Commit documentation**

```bash
git add docs/guides/nzbdav-migration.md
git commit -m "chore(docs): explain canary VFS cache evidence"
```

### Task 4: Verify and package the branch CLI

**Files:**
- No source changes expected.

- [ ] **Step 1: Run fresh focused verification**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~CanaryPerformance|FullyQualifiedName~CanaryCacheEvidence'
git diff --check
git status --short
```

Expected: zero failed tests, no diff errors, and a clean branch.

- [ ] **Step 2: Publish a self-contained versioned tool directory**

```bash
branch_sha=$(git rev-parse HEAD)
publish_dir="/tmp/infinidysk-tool-${branch_sha:0:8}"
dotnet publish tools/NzbDavMigration/NzbDavMigration.csproj -c Release \
  -r linux-x64 --self-contained true -p:RapidYencRuntimeIdentifier=linux-x64 \
  -o "$publish_dir"
```

Expected: publish exits zero and `$publish_dir/NzbDavMigration` exists.

- [ ] **Step 3: Install without replacing the deployed application**

Transfer the directory to nuc-1, install it root-only at
`/opt/infinidysk-migration/tools/<short-sha>`, checksum every file, and update only
`/opt/infinidysk-migration/tools/current` to the new version. Retain all earlier tool
versions. Do not recreate any container.

Expected: `NzbDavMigration benchmark-links --help` displays the command and all
application/rclone container start times remain unchanged.

### Task 5: Select and benchmark six additional canary files

**Files:**
- Create on nuc-1 evidence only: `benchmark-selection-additional.json`
- Create on nuc-1 evidence only: `cache-ranges-before-additional.json`
- Create on nuc-1 evidence only: `performance-additional-six/`

- [ ] **Step 1: Create the reviewed selection from the existing exact plan**

Select these exact paths and derive IDs/sizes from the checksummed plan rather than
typing identifiers manually:

```text
Movies-4K/Ash (2025)/Ash (2025) WEBDL-2160p.mkv
Movies-4K/One Night Only (2026)/One Night Only (2026) WEBDL-2160p.mkv
Movies-HD/Evil Dead Burn (2026)/Evil Dead Burn (2026) Remux-1080p.mkv
TV-4K/Outlander - Blood of My Blood/Season 1/Outlander - Blood of My Blood - S01E10 - Something Borrowed WEBDL-2160p.mkv
TV-HD/Outlander - Blood of My Blood/Season 1/Outlander - Blood of My Blood - S01E01 - Providence WEBDL-1080p Proper.mkv
TV-HD/The Lowdown (2025)/Season 1/The Lowdown (2025) - S01E02 - The Devil's Mama WEBDL-1080p.mkv
```

Mark only Evil Dead Burn as the large-file case. Confirm exactly three direct and
three RAR/multipart representations, six unique legacy IDs, six unique InfiniDysk
IDs, and no overlap with the original benchmark selection.

- [ ] **Step 2: Snapshot authoritative ranges and service state**

For both rclone cache roots, save each selected file's metadata size, normalized
`Rs`, cached-byte sum, and coverage percentage. Save `core/stats`, container image,
revision, health, restart counts, mount identity, `/mnt/plex` count, and
`/mnt/plex2` count. Do not reset counters.

- [ ] **Step 3: Run the standard six-file matrix**

```bash
/opt/infinidysk-migration/tools/current/NzbDavMigration benchmark-links \
  --selection "$E/benchmark-selection-additional.json" \
  --plan "$E/canary-plan/plan.json" \
  --output "$E/performance-additional-six" \
  --legacy-root /mnt/plex \
  --infinidysk-root /mnt/plex2 \
  --legacy-url http://192.168.20.65:8081 \
  --legacy-route direct-backend \
  --infinidysk-url http://192.168.20.65:8080 \
  --infinidysk-route direct-backend \
  --legacy-cache-root /mnt/nzbdav-cache/vfs/nzbdav \
  --infinidysk-cache-root /opt/docker/rclone-infinidysk/cache/vfs/infinidysk \
  --timeout-seconds 180
```

Expected: 6 files, 96 observations, explicit cold/partial/warm evidence, and every
timeout/error retained rather than hidden.

### Task 6: Compare direct WebDAV with mounted reads on uncached gaps

**Files:**
- Create on nuc-1 evidence only: `cache-ranges-before-route-comparison.json`
- Create on nuc-1 evidence only: `route-comparison-results.json`
- Create on nuc-1 evidence only: `route-comparison-results.md`

- [ ] **Step 1: Choose safe uncached windows**

Normalize the second `vfsMeta.Rs` snapshot and find two non-overlapping 32 MiB gaps
per file and side. Reject a file/side if two such gaps do not exist; record the
exclusion and select another non-benchmarked exact plan row with the same
representation/resolution role. Never delete or truncate cache files.

- [ ] **Step 2: Run the bounded paired harness**

Use an inline Python 3 harness under `sudo` that:

1. Loads only the selection, plan, and saved range map.
2. Uses `subprocess.Popen` with argument arrays, never a shell.
3. Reads direct data with `docker exec rclone-nzbdav rclone cat nzbdav:PATH` or
   `docker exec rclone-infinidysk rclone cat infinidysk:PATH`, passing exact offset
   and `--count 33554432`.
4. Reads mount data with `open`, `seek`, and repeated `read` calls.
5. Measures first-byte and completion monotonic timestamps, actual bytes, and MiB/s.
6. Kills the child process on a 120-second timeout.
7. Alternates route order and window assignment by file index.
8. Runs first and repeat passes for legacy-direct, legacy-mount,
   infinidysk-direct, and infinidysk-mount.
9. Emits JSON to stdout; redirect stdout to the create-new evidence file.

Expected: 48 observations (6 files × 4 routes × 2 passes), each with exactly
33,554,432 requested bytes or an explicit error/timeout.

- [ ] **Step 3: Render a Markdown comparison**

Use `jq` to produce one row per file/route/pass containing cache state, offset,
TTFB, completion milliseconds, MiB/s, actual/requested bytes, and error. Preserve
the JSON as the authoritative raw report.

### Task 7: Attribute the bottleneck and seal production evidence

**Files:**
- Create on nuc-1 evidence only: `diagnostic-state-after.txt`
- Create on nuc-1 evidence only: `diagnostic-analysis.md`
- Update on nuc-1 evidence only: `SHA256SUMS`

- [ ] **Step 1: Capture after-state without intervention**

Capture the same service, rclone, mount, link-count, health, revision, and restart
fields as Task 5. Add focused InfiniDysk and rclone warning/error counts bounded to
the diagnostic start timestamp. Do not restart or reset statistics.

- [ ] **Step 2: Write evidence-based attribution**

Apply these rules to each file and representation:

```text
direct slow + mount slow     => backend/NNTP/archive cold path
direct fast + mount slow     => rclone VFS/cache path
both fast only on repeat     => expected cache-fill effect
RAR-only slowdown            => multipart/archive path
direct-only slowdown         => backend range handling or provider path
```

State uncertainty where route ordering, existing backend cache, or provider
variation prevents attribution. Do not average away timeouts or excluded rows.

- [ ] **Step 3: Verify safety invariants**

Require:

```text
InfiniDysk exact branch tool SHA recorded
InfiniDysk application image/revision unchanged
application and adjacent restart counts unchanged
/mnt/plex count unchanged from the fresh baseline or every external drift explained
/mnt/plex2 exactly 30 links
zero containers mounting /mnt/plex2
both rclone mounts healthy
Plex and Arr untouched
```

- [ ] **Step 4: Rebuild and verify the evidence manifest**

Recreate root `SHA256SUMS` from every evidence file except the manifest and its
temporary name, then run `sha256sum -c SHA256SUMS` and require exit zero.

### Task 8: Push the focused repository change and open a PR

**Files:**
- No additional source changes expected.

- [ ] **Step 1: Verify branch state and commits**

```bash
git status --short --branch
git log --oneline origin/main..HEAD
git diff --check origin/main...HEAD
```

Expected: clean branch with focused design, classifier, report, test, and guide
commits only.

- [ ] **Step 2: Push the exact branch and open the PR**

```bash
git push -u origin fix/canary-cache-evidence
gh pr create --repo johoja12/infinidysk --base main \
  --head fix/canary-cache-evidence \
  --title "fix(rclone): report real cache coverage in canary benchmarks" \
  --body-file /tmp/canary-cache-evidence-pr.md
```

The PR body must summarize the sparse-file bug, structured `vfsMeta` fix, focused
test result, setup-wizard impact (`none`: migration CLI only), and production test
plan. Do not merge or enable auto-merge.

- [ ] **Step 3: Verify remote handoff and reset the primary workspace**

Verify the PR head SHA equals local `HEAD`. Confirm `/opt/projects/infinidysk` is a
clean `main` tracking `origin/main`. Leave the feature worktree available for PR
follow-up without changing the primary workspace.
