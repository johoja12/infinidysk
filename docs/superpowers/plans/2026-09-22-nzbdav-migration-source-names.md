# NzbDav Migration Source Names Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve authoritative legacy NzbDav release names through checksummed export, normal InfiniDysk submission, and media-name fallback, then replace and verify the 16-release Phase B canary.

**Architecture:** Add optional source filename/job-name metadata to the existing schema-v1/v2 release record so old packages remain readable. New history-backed exports populate exact legacy values; recovered orphan batches derive a deterministic job name from the legacy content path. The scanner submits with the source filename and validates that normal queue naming produces the recorded job name, while `PayloadPath` remains private package storage metadata.

**Tech Stack:** .NET 10, C# records and `System.Text.Json`, xUnit, EF Core SQLite migration ledger, PostgreSQL production store, Docker Compose, rclone/WebDAV, GitHub CLI.

---

### Task 1: Protect the manifest naming contract

**Files:**

- Modify: `backend/UsenetMigration/NzbDav/NzbDavExportManifest.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavExportManifestTests.cs`

- [ ] **Step 1: Write failing round-trip and validation tests**

Extend `SampleManifest()` with explicit source names and assert they survive serialization:

```csharp
var release = Assert.Single(restored.Releases);
Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb", release.SourceFileName);
Assert.Equal("Outlander.Blood.of.My.Blood.S02E01", release.SourceJobName);
```

Add theory cases proving the manifest rejects half-present metadata, an unsafe
filename such as `../release.nzb`, a blank job name, and a job name inconsistent
with `NzbDavNaming.JobName(SourceFileName)`.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 --no-restore \
  --filter 'FullyQualifiedName~NzbDavExportManifestTests'
```

Expected: compilation/test failure because `SourceFileName` and `SourceJobName`
do not exist or invalid values are accepted.

- [ ] **Step 3: Add optional release naming fields and validation**

Append optional fields so positional callers and old package JSON remain compatible:

```csharp
public sealed record NzbDavExportRelease(
    string SourceReleaseId,
    Guid? NzbBlobId,
    string PayloadPath,
    IReadOnlyList<NzbDavExportLeaf> Leaves,
    string? SourceFileName = null,
    string? SourceJobName = null);
```

In `Validate()`, require both fields or neither. When present, require a basename-only
nonblank filename, a nonblank job name, and:

```csharp
var expectedJobName = NzbDavNaming.JobName(release.SourceFileName);
if (!string.Equals(expectedJobName, release.SourceJobName, StringComparison.Ordinal))
    throw new InvalidDataException("NzbDav source filename and job name disagree.");
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the command from Step 2. Expected: all `NzbDavExportManifestTests` pass.

- [ ] **Step 5: Commit the contract**

```bash
git add backend/UsenetMigration/NzbDav/NzbDavExportManifest.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavExportManifestTests.cs
git commit -m "fix(migration): validate source release names"
```

### Task 2: Export authoritative source names

**Files:**

- Create: `tools/NzbDavMigration/Export/NzbDavSourceNameResolver.cs`
- Modify: `tools/NzbDavMigration/Export/CanaryPackageWriter.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavSourceNameResolverTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryPackageWriterTests.cs`

- [ ] **Step 1: Write failing resolver and package-writer tests**

Cover two explicit paths:

```csharp
Assert.Equal(
    new NzbDavSourceNames("Outlander.S02E01.nzb", "Outlander.S02E01"),
    NzbDavSourceNameResolver.FromHistory("Outlander.S02E01.nzb", "Outlander.S02E01"));

Assert.Equal(
    new NzbDavSourceNames("Recovered.Show.S01E01.nzb", "Recovered.Show.S01E01"),
    NzbDavSourceNameResolver.FromLegacyPaths(
        ["/content/sonarr/Recovered.Show.S01E01/episode.mkv"]));
```

Also assert `CanaryPackageWriter` copies the names from `CanaryExportRelease` into
the checksummed manifest.

- [ ] **Step 2: Run the tests and verify RED**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 --no-restore \
  --filter 'FullyQualifiedName~NzbDavSourceNameResolverTests|FullyQualifiedName~CanaryPackageWriterTests'
```

Expected: compilation failure because the resolver and export fields do not exist.

- [ ] **Step 3: Implement the resolver and writer propagation**

Define:

```csharp
public sealed record NzbDavSourceNames(string FileName, string JobName);

public static class NzbDavSourceNameResolver
{
    public static NzbDavSourceNames FromHistory(string? fileName, string? jobName);
    public static NzbDavSourceNames FromLegacyPaths(IEnumerable<string> legacyPaths);
}
```

`FromHistory` must reject missing or inconsistent authoritative fields.
`FromLegacyPaths` must require every path to have the shape
`/content/<category>/<job>/...`, require exactly one common job component, sanitize
it with the existing path rules, and return `<job>.nzb` plus `<job>`.

Append `SourceFileName` and `SourceJobName` to `CanaryExportRelease`; pass them into
`NzbDavExportRelease` in `CanaryPackageWriter`.

- [ ] **Step 4: Populate history-backed and orphan-recovery exports**

In the canary `export` flow, require one distinct `HistoryFileName` and
`HistoryJobName` per grouped release and use `FromHistory`. In `export-batches`,
use `FromLegacyPaths` over the recovered leaves. Pass the resolved names into every
`CanaryExportRelease`.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass.

- [ ] **Step 6: Commit exporter behavior**

```bash
git add tools/NzbDavMigration/Export/NzbDavSourceNameResolver.cs \
  tools/NzbDavMigration/Export/CanaryPackageWriter.cs \
  tools/NzbDavMigration/Program.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavSourceNameResolverTests.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryPackageWriterTests.cs
git commit -m "fix(migration): export authoritative release names"
```

### Task 3: Submit imports with source names

**Files:**

- Modify: `backend/UsenetMigration/Runner/NzbDavScanRunner.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavScanRunnerTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryEndToEndTests.cs`

- [ ] **Step 1: Write the failing scanner regression**

Create a package whose internal payload is `payloads/<guid>.nzb` but whose release
metadata contains `Outlander.Blood.of.My.Blood.S02E01.nzb` and the matching job
name. Assert the persisted row is human-readable:

```csharp
Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb", release.SubmitFileName);
Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb", release.QueueFileName);
Assert.Equal("Outlander.Blood.of.My.Blood.S02E01", release.JobName);
```

Add a separate old-package test with null source fields that explicitly expects the
existing payload-basename fallback.

- [ ] **Step 2: Run the scanner tests and verify RED**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 --no-restore \
  --filter 'FullyQualifiedName~NzbDavScanRunnerTests'
```

Expected: the new-package test reports the UUID payload basename instead of the
source filename.

- [ ] **Step 3: Use source metadata during scanning**

Replace the unconditional payload basename with:

```csharp
var submitName = source.SourceFileName ?? Path.GetFileName(source.PayloadPath);
var queueName = NzbDavNaming.QueueFileName(submitName);
var jobName = NzbDavNaming.JobName(submitName);
```

Keep `MetaPath = source.PayloadPath`; payload storage and display naming must stay
separate.

- [ ] **Step 4: Strengthen the end-to-end assertion**

Update the fixture to use a UUID payload path and a human-readable source name.
Assert the scan stage preserves the human-readable release names and demonstrate
the existing deobfuscation fallback contract directly:

```csharp
Assert.Equal(
    "Outlander.Blood.of.My.Blood.S02E01.mkv",
    ImportableVideoNamer.Normalize(
        "b082fa0beaa644d3aa01045d5b8d0b36.xyz",
        ".mkv",
        release.JobName,
        allowBaseRename: true));
```

- [ ] **Step 5: Run scanner and end-to-end tests and verify GREEN**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 --no-restore \
  --filter 'FullyQualifiedName~NzbDavScanRunnerTests|FullyQualifiedName~NzbDavCanaryEndToEndTests'
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit importer behavior**

```bash
git add backend/UsenetMigration/Runner/NzbDavScanRunner.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavScanRunnerTests.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryEndToEndTests.cs
git commit -m "fix(migration): submit imports with source release names"
```

### Task 4: Document name preservation and replacement safety

**Files:**

- Modify: `docs/guides/nzbdav-migration.md`
- Modify: `DEPLOYMENT-NOTES.md`

- [ ] **Step 1: Document the package distinction**

State that `PayloadPath` is an internal checksummed locator and that new exports
carry authoritative `SourceFileName`/`SourceJobName`. State that packages lacking
these fields remain readable but must be regenerated for name-preserving imports.

- [ ] **Step 2: Document the Phase B replacement boundary**

Record that only dedicated `migration-*` imports and `/mnt/plex2` are eligible for
the replacement procedure. Explicitly exclude legacy NzbDav, `/mnt/plex`, Plex,
Arr, and unrelated InfiniDysk history.

- [ ] **Step 3: Verify documentation formatting**

```bash
cd frontend && npx prettier --check \
  ../docs/guides/nzbdav-migration.md \
  ../DEPLOYMENT-NOTES.md
```

Expected: both files pass formatting.

- [ ] **Step 4: Commit documentation**

```bash
git add docs/guides/nzbdav-migration.md DEPLOYMENT-NOTES.md
git commit -m "chore(docs): document migration name preservation"
```

### Task 5: Verify and hand off the source fix

**Files:**

- Verify only; no new files expected.

- [ ] **Step 1: Run the focused rebuilt test slice**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~NzbDavExportManifestTests|FullyQualifiedName~CanaryPackageWriterTests|FullyQualifiedName~NzbDavSourceNameResolverTests|FullyQualifiedName~NzbDavScanRunnerTests|FullyQualifiedName~NzbDavCanaryEndToEndTests'
```

Expected: zero failures.

- [ ] **Step 2: Verify repository state and commit range**

```bash
git diff --check origin/main...HEAD
git status --short
git log --oneline origin/main..HEAD
```

Expected: no whitespace errors, clean worktree, focused conventional commits.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin fix/migration-source-names
gh pr create --repo johoja12/infinidysk --base main \
  --head fix/migration-source-names \
  --title "fix(migration): preserve release names when importing NzbDav libraries" \
  --body "Closes #25. Preserves authoritative source names independently of UUID payload storage, retains old-package compatibility, and adds focused migration regressions."
```

Expected: a PR in `johoja12/infinidysk`. Do not merge or enable auto-merge; a human
maintainer must merge it.

### Task 6: Deploy and replace the Phase B canary after human merge

**Files:**

- Production paths on `ubuntu@192.168.20.65` only.
- Delete: `/opt/infinidysk-migration/input/nzbdav-canary`
- Delete: `/opt/infinidysk-migration/canary/phase-b-20260920T184834Z`
- Delete: the superseded Phase B plan/apply/evidence directories identified by
  their package digest and run ID during fresh reconciliation.

- [ ] **Step 1: Prove the merge and build exact artifacts**

Fetch `origin/main`, require the PR merge commit, build `local/infinidysk:<short-sha>`,
and publish the self-contained migration CLI to
`/opt/infinidysk-migration/tools/<short-sha>`. Verify image revision labels and CLI
SHA-256 before changing the running application.

- [ ] **Step 2: Deploy only InfiniDysk and verify readiness**

Update the existing image override, recreate only `infinidysk`, and require frontend,
backend, public health, unauthenticated WebDAV `401`, zero restarts, readable legacy
and InfiniDysk mounts, and the exact merged revision. Do not restart NzbDav, either
rclone, PostgreSQL, Plex, or Arr.

- [ ] **Step 3: Fresh-validate the destructive target**

Require all of the following immediately before deletion:

- migration session has no active scan/submission/reconciliation;
- the migration ledger resolves exactly 16 completed Nzo IDs;
- those IDs resolve to exactly 16 `migration-*` history rows, 16 download
  directories, and 34 file leaves;
- `/mnt/plex2` contains exactly 30 symlink leaves, only the expected empty parent
  directories, and zero regular files or unexpected symlinks;
- none of the target IDs occurs in non-migration history or outside the dedicated
  content roots.

Stop on any count or ownership mismatch.

- [ ] **Step 4: Delete the imported content through supported APIs**

Submit the exact 16 Nzo IDs to SAB history deletion with
`del_completed_files=1`. Poll cleanup until all 16 history rows, 16 download
directories, 34 leaves, related blobs, and native-cache entries are absent. Stop if
any unrelated row changes.

- [ ] **Step 5: Remove the old parallel-library and migration artifacts**

After revalidating every `/mnt/plex2` entry is a canary symlink, remove all 30 links
and empty parent directories. Call `POST /api/migration/altmount/reset`, verify the
session is reset, then call `POST /api/migration/altmount/migration-data/forget`
with `{ "confirm": true }` and verify the old migration records are absent. Delete
the obsolete input package, Phase B working directory, old plan, journal, and
evidence. Preserve the migration tools installation itself for the regenerated run.

- [ ] **Step 6: Regenerate and import with the fixed toolchain**

Re-inventory the same 30 legacy `/mnt/plex` links, export a fresh checksummed
package with source naming fields, and verify its manifest before connecting it to
InfiniDysk. Use one worker, queue depth five, and the same dedicated category map.
Run to terminal completion, reconcile, and require 30 exact correlations before
generating or applying a plan.

- [ ] **Step 7: Verify names before creating links**

Query the 16 new history rows and their DAV descendants. Require:

- every history filename and job name equals the source manifest value;
- zero job names match the UUID-only pattern;
- zero media leaves use an unintended UUID basename;
- the Outlander example is mounted beneath
  `Outlander.Blood.of.My.Blood.S02E01.2160p.HMAX.WEB-DL.DD_5.1.H.265-playWEB`
  with a human-readable `.mkv` leaf.

On failure, stop with `/mnt/plex2` empty.

- [ ] **Step 8: Recreate and validate `/mnt/plex2`**

Generate a new immutable plan and apply exactly 30 create-only links. Require exact
target sizes, bounded head/middle/tail reads, representative direct and archive
seeks, byte equality against legacy for sampled ranges, healthy containers, and no
naming/streaming errors. Confirm `/mnt/plex2` remains unregistered with Plex and
Arr and `/mnt/plex` is unchanged.
