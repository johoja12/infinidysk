# NzbDav Full-Library Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover at least 90% of the live NzbDav-backed Plex library from retained and orphan NZBs, import it through InfiniDysk in bounded resumable batches, and create a validated parallel `/mnt/plex2` tree that targets only InfiniDysk.

**Architecture:** Extend the read-only host migration CLI with immutable full-library inventories, a private resumable SQLite orphan-NZB catalogue, deterministic article-evidence recovery, and checksummed batch packages. Extend the backend with stable path-independent archive identities, idempotent reconciliation of completed runs, exact-only plan gates, and aggregate batch progress. Apply plans create-only on nuc-1, measure coverage against a fresh source snapshot, and stop before Plex or Arr registration.

**Tech Stack:** .NET 10, xUnit, Npgsql, Microsoft.Data.Sqlite, EF Core migration ledger, ASP.NET Core controllers, React Router 8/Vitest, SHA-256 manifests, POSIX symlinks, rclone/WebDAV, ffprobe.

---

## File map

New host-tool files are grouped by responsibility:

- `tools/NzbDavMigration/Catalogue/OrphanCatalogueModels.cs` — immutable catalogue records and terminal classifications.
- `tools/NzbDavMigration/Catalogue/OrphanCatalogueStore.cs` — private SQLite schema, bounded transactions, resume state, and completed-summary seal.
- `tools/NzbDavMigration/Catalogue/OrphanCatalogueScanner.cs` — frozen regular-file inventory, safe NZB parsing, digest/article extraction, and mutation checks.
- `tools/NzbDavMigration/Recovery/LegacySourceRecovery.cs` — direct/archive evidence matching and complete per-link accounting.
- `tools/NzbDavMigration/Recovery/FullRecoveryModels.cs` — checksummed inventory, recovery report, and master-manifest contracts.
- `tools/NzbDavMigration/Recovery/BatchPackagePlanner.cs` — deterministic whole-release partitioning by release and payload-byte limits.
- `tools/NzbDavMigration/Canary/CanaryCoverageReporter.cs` — final source/link/journal reconciliation and 90% gate.

New backend files are similarly bounded:

- `backend/UsenetMigration/Provenance/NzbDavReconciliationService.cs` — terminal-run, no-submission correlation refresh.
- `backend/UsenetMigration/NzbDav/NzbDavStableArchiveIdentity.cs` — path-independent archive-part hashing.
- `backend/UsenetMigration/Model/NzbDavBatchModels.cs` — master/batch status response contracts.

Existing files remain owners of their current concerns: package validation stays in
`NzbDavPackageReader`, planning stays in `NzbDavCanaryLinkPlanner`, and host link
mutation stays in `CanaryLinkApplier`.

### Task 1: Make canary plans fail closed on any non-exact selection

**Files:**
- Modify: `backend/UsenetMigration/Canary/NzbDavCanaryLinkPlanner.cs`
- Modify: `backend/Api/Controllers/UsenetMigration/NzbDavMigrationController.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryLinkPlannerTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationControllerTests.cs`

- [ ] **Step 1: Replace the permissive planner test with a failing completeness test**

Change the first planner test to assert that one exact plus one unmatched selection
throws before rows or plan files are written:

```csharp
var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
    new NzbDavCanaryLinkPlanner().GenerateAsync(db, package, 42, output));
Assert.Contains("every selected", error.Message, StringComparison.OrdinalIgnoreCase);
Assert.Empty(await db.CanaryLinks.ToListAsync());
Assert.False(Directory.Exists(output));
```

Add a controller test with `selectedCount == 2`, `exactCount == 1`, and no
ambiguous rows; assert `GenerateCanaryPlan()` returns `BadRequestObjectResult`.

- [ ] **Step 2: Run the focused tests and confirm the unsafe behavior**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzbDavCanaryLinkPlannerTests|FullyQualifiedName~NzbDavMigrationControllerTests'
```

Expected: the new unmatched-row tests fail because the current planner considers a
partially actionable plan valid and the controller checks only ambiguity.

- [ ] **Step 3: Enforce equality of selected, exact, and actionable counts**

Before opening a transaction in `NzbDavCanaryLinkPlanner.GenerateAsync`, reject
the computed links unless every row is exact and has a target:

```csharp
var nonExact = links.Where(link =>
    link.CorrelationStatus != "exact"
    || link.NewRelativeTarget is null
    || link.ApplyStatus != "planned").ToArray();
if (nonExact.Length != 0)
    throw new InvalidDataException(
        $"Every selected link must be exact and actionable; {nonExact.Length} row(s) are not.");
```

Set `IsValid` only from the strict equality:

```csharp
var actionable = links.Count(link => link.NewRelativeTarget is not null);
var valid = links.Count == package.Manifest.SelectedLinks.Count
            && actionable == links.Count
            && links.All(link => link.CorrelationStatus == "exact");
```

In the controller, reject whenever `report.ExactCount != report.SelectedCount` or
`report.ExclusionCount != 0`; include only counts in the error.

- [ ] **Step 4: Re-run the focused tests**

Expected: all planner/controller tests pass and no incomplete plan is persisted.

- [ ] **Step 5: Commit the safety fix**

```bash
git add backend/UsenetMigration/Canary/NzbDavCanaryLinkPlanner.cs \
  backend/Api/Controllers/UsenetMigration/NzbDavMigrationController.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryLinkPlannerTests.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationControllerTests.cs
git commit -m "fix(migration): block incomplete NzbDav link plans"
```

### Task 2: Add stable archive article identities

**Files:**
- Create: `backend/UsenetMigration/NzbDav/NzbDavStableArchiveIdentity.cs`
- Modify: `backend/UsenetMigration/NzbDav/NzbDavArticleIdentity.cs`
- Modify: `tools/NzbDavMigration/Export/LegacyIdentityExtractor.cs`
- Modify: `backend/UsenetMigration/Provenance/DavItemArticleIdentityReader.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavArticleIdentityTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/LegacyCompatibilityTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCorrelationProviderTests.cs`

- [ ] **Step 1: Write failing rename and range-disambiguation tests**

Define stable part input in the tests:

```csharp
var parts = new[]
{
    new NzbDavArchivePartIdentity(
        [new NzbDavArticleSegment(1, 100, "one@test")],
        SegmentStart: 0, SegmentLength: 100,
        FileStart: 4096, FileLength: 80),
};
var first = NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, 80);
var renamed = NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, 80);
Assert.Equal(first, renamed);
Assert.NotEqual(first, NzbDavStableArchiveIdentity.Compute(
    releaseDigest, [parts[0] with { FileStart = 8192 }], 80));
```

Add a legacy extractor test where `LegacyPath` differs from the future published
name but identical part evidence produces `archive-articles-v2`. Add a target
reader test for eager multipart metadata and lazy metadata including both
`FileParts` and `PendingParts`.

- [ ] **Step 2: Run identity tests and confirm the new API is absent**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzbDavArticleIdentityTests|FullyQualifiedName~LegacyCompatibilityTests|FullyQualifiedName~NzbDavCorrelationProviderTests'
```

Expected: compilation fails for `NzbDavArchivePartIdentity` and
`NzbDavStableArchiveIdentity`.

- [ ] **Step 3: Implement the path-independent identity contract**

Create:

```csharp
public sealed record NzbDavArchivePartIdentity(
    IReadOnlyList<NzbDavArticleSegment> Segments,
    long SegmentStart,
    long SegmentLength,
    long FileStart,
    long FileLength);

public static class NzbDavStableArchiveIdentity
{
    public const string Kind = "archive-articles-v2";

    public static string Compute(
        string releaseDigest,
        IReadOnlyList<NzbDavArchivePartIdentity> parts,
        long fileSize) => NzbDavArticleIdentity.ComputeArchiveArticles(
            releaseDigest, parts, fileSize);
}
```

Expose an internal hash primitive from `NzbDavArticleIdentity` and append, in
order, the release digest, part count, resolved article segments, segment/file
range coordinates, and exact file size. Reject empty parts, negative ranges,
overflow, and invalid release digests.

Update the legacy extractor and target reader to build identical parts from
`DavRarFile.RarPart` and `DavMultipartFile.FilePart`. Include lazy `PendingParts`
with their segment range and estimated contribution only when exact persisted
ranges exist; otherwise return missing identity so a guessed lazy range never
becomes exact.

- [ ] **Step 4: Run the identity tests**

Expected: stable identities survive published renames, differ on range or article
changes, and legacy/target readers agree.

- [ ] **Step 5: Commit stable identities**

```bash
git add backend/UsenetMigration/NzbDav tools/NzbDavMigration/Export/LegacyIdentityExtractor.cs \
  backend/UsenetMigration/Provenance/DavItemArticleIdentityReader.cs \
  tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): identify archive members independently of names"
```

### Task 3: Reconcile completed runs without resubmission

**Files:**
- Create: `backend/UsenetMigration/Provenance/NzbDavReconciliationService.cs`
- Modify: `backend/Api/Controllers/UsenetMigration/NzbDavMigrationController.cs`
- Modify: `backend/Program.cs`
- Modify: `backend/UsenetMigration/Provenance/NzbDavCorrelationProvider.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavReconciliationServiceTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationControllerTests.cs`

- [ ] **Step 1: Write failing service tests for the Phase B cases**

Seed a completed NzbDav run with one already exact direct file, one archive file
whose `PathInArchive` identity matches, one unique same-size archive target, and
one release with two same-size targets. Assert:

```csharp
var result = await service.ReconcileAsync(runId, packageRoot);
Assert.Equal(3, result.ExactCount);
Assert.Equal(1, result.AmbiguousCount);
Assert.Equal(0, await dav.QueueItems.CountAsync());
Assert.Equal(historyCountBefore, await dav.HistoryItems.CountAsync());
Assert.Equal(itemCountBefore, await dav.Items.CountAsync());
```

Run it twice and assert the second result and persisted target IDs are identical.
Seed an existing exact mapping to another target and assert reconciliation throws
without changing it.

- [ ] **Step 2: Run the new tests and confirm the service is absent**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzbDavReconciliationServiceTests|FullyQualifiedName~NzbDavMigrationControllerTests'
```

Expected: compilation fails for `NzbDavReconciliationService`.

- [ ] **Step 3: Implement strict legacy-package fallback and reconciliation**

Add a correlation overload receiving release provenance:

```csharp
public sealed record NzbDavCorrelationScope(
    long RunId,
    string SourceReleaseId,
    Guid ImportedNzoId,
    bool AllowLegacyUniqueSizeFallback);
```

Only when `AllowLegacyUniqueSizeFallback` is true and stable/path identity found
no match, select targets with exact size inside that imported NzoId. Return exact
only for one video target, with match method
`legacy-release-payload-size-unique`; zero stays unmatched and multiple becomes
ambiguous. Never inspect names for this fallback.

`NzbDavReconciliationService` must:

```csharp
public Task<NzbDavReconciliationResult> ReconcileAsync(
    long runId, string packageRoot, CancellationToken cancellationToken = default)
```

Validate the package digest against the session, require terminal session/run and
zero active submissions, load each `MigratedRelease` for the run, resolve leaves
by its persisted `NzoId`, compute correlations, and update `MigrationReleaseFile`,
`MigratedFile`, and mapped counts in one migration-ledger transaction. It must not
call queue, SAB, payload builder, or submission services.

Expose authenticated `POST /api/migration/nzbdav/reconcile`. Return run ID,
selected, exact, ambiguous, unmatched, and `submittedCount: 0`.

- [ ] **Step 4: Re-run focused tests and add a zero-write assertion**

Expected: tests pass twice; queue/history/DAV counts do not change.

- [ ] **Step 5: Commit reconciliation**

```bash
git add backend/UsenetMigration/Provenance backend/Api/Controllers/UsenetMigration \
  backend/Program.cs tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): reconcile completed NzbDav imports safely"
```

### Task 4: Preserve missing-history article metadata in full inventories

**Files:**
- Modify: `tools/NzbDavMigration/Legacy/LegacyNzbDavReader.cs`
- Modify: `tools/NzbDavMigration/Inventory/LibraryInventoryService.cs`
- Create: `tools/NzbDavMigration/Recovery/FullRecoveryModels.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/LegacyNzbDavReaderTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/LibraryInventoryServiceTests.cs`

- [ ] **Step 1: Change the PostgreSQL fixture expectation to require metadata**

For the row without history, assert:

```csharp
var orphan = result.Items.Single(item => item.Id == withoutHistory);
Assert.Null(orphan.HistoryItemId);
Assert.Equal("[\"two@example\"]", orphan.NzbSegmentsJson);
Assert.Null(orphan.ResolutionExclusion);
```

Add tests for missing-history RAR/multipart rows and invalid ancestry. Invalid
ancestry remains excluded even when metadata exists.

- [ ] **Step 2: Run reader/inventory tests and confirm the current suppression**

Expected: the missing-history metadata assertion fails because joins are guarded
by `h.Id IS NOT NULL` and the row is classified `missing-history`.

- [ ] **Step 3: Load metadata independently from history ownership**

Remove `AND h."Id" IS NOT NULL` from the three metadata joins. Split ownership
from recoverability:

```csharp
string? HistoryExclusion = historyId is null ? "missing-history" : null;
string? SafetyExclusion = invalidOrUnsafeReason;
```

Extend `LegacyDavItemRow` with those two fields while preserving
`ResolutionExclusion` for safety-only exclusions. `LibraryInventoryService`
emits `recoverable-orphan` when history is missing but type metadata and size are
present; it emits `excluded` for safety failures or missing metadata.

Create versioned full inventory records with source snapshot digest inputs and a
terminal classification field.

- [ ] **Step 4: Run focused tests**

Expected: retained and missing-history rows carry metadata, while health/ancestry
failures remain excluded.

- [ ] **Step 5: Commit inventory support**

```bash
git add tools/NzbDavMigration/Legacy tools/NzbDavMigration/Inventory \
  tools/NzbDavMigration/Recovery tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): inventory orphaned NzbDav library metadata"
```

### Task 5: Add the private resumable orphan catalogue

**Files:**
- Modify: `tools/NzbDavMigration/NzbDavMigration.csproj`
- Create: `tools/NzbDavMigration/Catalogue/OrphanCatalogueModels.cs`
- Create: `tools/NzbDavMigration/Catalogue/OrphanCatalogueStore.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/OrphanCatalogueStoreTests.cs`

- [ ] **Step 1: Write failing store tests**

Test a new store, bounded upserts, duplicate-payload grouping, cancelled partial
state, input-list digest mismatch, and completed sealing:

```csharp
await store.BeginAsync(inputDigest);
await store.UpsertAsync(file);
await store.SealAsync(summary);
await Assert.ThrowsAsync<InvalidOperationException>(() =>
    store.BeginAsync(new string('b', 64)));
Assert.Equal("complete", (await store.ReadStateAsync()).Status);
```

Assert the database and sidecar summary are mode `0600` on Linux.

- [ ] **Step 2: Run the store tests and confirm missing types**

Expected: compilation fails for `OrphanCatalogueStore`.

- [ ] **Step 3: Implement schema and state transitions**

Add an explicit `Microsoft.Data.Sqlite` package reference. Create tables:

```sql
CREATE TABLE CatalogueState(
  Id INTEGER PRIMARY KEY CHECK(Id=1), InputDigest TEXT NOT NULL,
  Status TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT);
CREATE TABLE Blobs(
  RelativePath TEXT PRIMARY KEY, Length INTEGER NOT NULL, MtimeTicks INTEGER NOT NULL,
  Sha256 TEXT, ParseStatus TEXT NOT NULL, FailureClass TEXT,
  ReleaseDigest TEXT, ArticleCount INTEGER NOT NULL DEFAULT 0);
CREATE TABLE Articles(
  MessageId TEXT NOT NULL, BlobPath TEXT NOT NULL, FileOrdinal INTEGER NOT NULL,
  SegmentOrdinal INTEGER NOT NULL, SegmentBytes INTEGER NOT NULL,
  PRIMARY KEY(MessageId,BlobPath,FileOrdinal,SegmentOrdinal));
CREATE INDEX IX_Articles_MessageId ON Articles(MessageId);
```

Use WAL only while actively building, commit every 100 blobs, checkpoint before
sealing, and write the checksummed completion summary atomically. Reject a resume
whose frozen input digest differs.

- [ ] **Step 4: Run store tests**

Expected: all store state, resume, and permission tests pass.

- [ ] **Step 5: Commit the catalogue store**

```bash
git add tools/NzbDavMigration/NzbDavMigration.csproj tools/NzbDavMigration/Catalogue \
  tests/NzbWebDAV.Tests/UsenetMigration/OrphanCatalogueStoreTests.cs
git commit -m "feat(migration): add resumable orphan NZB catalogue"
```

### Task 6: Scan orphan blobs safely and resumably

**Files:**
- Create: `tools/NzbDavMigration/Catalogue/OrphanCatalogueScanner.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/OrphanCatalogueScannerTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/LegacyBlobResolverTests.cs`

- [ ] **Step 1: Write failing scanner tests**

Cover regular NZB, standard PUBLIC/SYSTEM DOCTYPE, internal entity, symlink,
changed-during-read, duplicate bytes, malformed XML, cancellation, and resume.
Inject a hook that changes a fixture between parse and post-read stat, then assert
`changed` rather than `valid`.

- [ ] **Step 2: Run scanner tests and confirm the command is absent**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~OrphanCatalogueScannerTests|FullyQualifiedName~LegacyBlobResolverTests'
```

Expected: compilation fails for the scanner.

- [ ] **Step 3: Implement frozen-list scanning**

Expose:

```csharp
public Task<OrphanCatalogueSummary> ScanAsync(
    string blobRoot,
    string frozenInventoryPath,
    OrphanCatalogueStore store,
    CancellationToken cancellationToken = default)
```

Create the frozen list with relative path, length, and mtime using no-follow
metadata. Open with `FileShare.Read`, reject reparse points before and after open,
compute SHA-256 while copying to a bounded temporary stream when needed, parse
with the hardened NZB reader, normalize message IDs, and re-stat before commit.
Store sanitized classes only; do not log article IDs or filenames from subjects.

Add CLI commands:

```text
catalogue-list --blob-root PATH --output FILE
catalogue-scan --blob-root PATH --inventory FILE --database FILE --summary FILE
```

Both outputs are create-new/private; `catalogue-scan` resumes only the same input
digest.

- [ ] **Step 4: Run scanner tests**

Expected: unsafe or changed inputs are classified, cancellation resumes, and
valid duplicate bytes share a digest.

- [ ] **Step 5: Commit the scanner**

```bash
git add tools/NzbDavMigration/Catalogue tools/NzbDavMigration/Program.cs \
  tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): catalogue orphan NZBs safely"
```

### Task 7: Recover every library link or classify it

**Files:**
- Create: `tools/NzbDavMigration/Recovery/LegacySourceRecovery.cs`
- Modify: `tools/NzbDavMigration/Recovery/FullRecoveryModels.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/LegacySourceRecoveryTests.cs`

- [ ] **Step 1: Write failing direct/archive/ambiguity tests**

Build a small catalogue with a retained-history payload, byte-identical orphan
duplicates, two distinct ambiguous payloads, and missing articles. Assert:

```csharp
Assert.Equal("exact-direct", rows[0].Classification);
Assert.Equal("exact-archive", rows[1].Classification);
Assert.Equal("ambiguous-payload", rows[2].Classification);
Assert.Equal("missing-articles", rows[3].Classification);
Assert.Equal(rows.Count, report.TotalLinks);
Assert.Equal(0.5m, report.RecoverableFraction);
```

Assert a name-only candidate remains missing and byte-identical payloads count as
one logical candidate.

- [ ] **Step 2: Run recovery tests and confirm the matcher is absent**

Expected: compilation fails for `LegacySourceRecovery`.

- [ ] **Step 3: Implement indexed evidence joins and the 90% gate**

For direct rows, intersect catalogue candidates for every normalized segment ID,
then verify ordered file segments and exact size. For RAR/multipart rows, intersect
all contributing IDs, build stable part evidence, and verify one logical payload.
Collapse equal SHA-256 payloads before deciding ambiguity.

Write immutable `recovery.json`, `master-manifest.json`, `exclusions.json`, and
`SHA256SUMS`. `master-manifest.json` contains every source relative path and its
terminal class. Add:

```text
recover-full --inventory FILE --catalogue FILE --output DIR --minimum-coverage 0.90
```

Return exit code 3 when completed recovery is below the threshold; do not emit
batch assignments in that case.

- [ ] **Step 4: Run recovery tests**

Expected: exact/duplicate/ambiguous/missing cases are deterministic and every
input row is accounted for.

- [ ] **Step 5: Commit recovery**

```bash
git add tools/NzbDavMigration/Recovery tools/NzbDavMigration/Program.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/LegacySourceRecoveryTests.cs
git commit -m "feat(migration): recover library links from orphan NZBs"
```

### Task 8: Partition and write bounded schema-v2 packages

**Files:**
- Create: `tools/NzbDavMigration/Recovery/BatchPackagePlanner.cs`
- Modify: `tools/NzbDavMigration/Export/CanaryPackageWriter.cs`
- Modify: `backend/UsenetMigration/NzbDav/NzbDavExportManifest.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryPackageWriterTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavPackageReaderTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/BatchPackagePlannerTests.cs`

- [ ] **Step 1: Write failing schema compatibility and partition tests**

Assert schema v1 still deserializes with optional stable fields null. Assert v2
requires master digest, batch index/count, payload digest, and stable identity for
archive leaves. Partition releases with limits:

```csharp
var batches = planner.Partition(releases, maxReleases: 2, maxPayloadBytes: 1_000);
Assert.Equal([2, 1], batches.Select(batch => batch.Releases.Count));
Assert.All(batches, batch => Assert.DoesNotContain(
    batch.Releases, release => batches.Count(other => other.Releases.Contains(release)) != 1));
```

Assert an oversized single release occupies one explicitly marked batch and is
never split.

- [ ] **Step 2: Run package tests and confirm schema v2 is rejected**

Expected: new schema/partition tests fail.

- [ ] **Step 3: Implement backward-compatible v2 and deterministic batches**

Add optional manifest fields:

```csharp
string? MasterManifestDigest,
int? BatchIndex,
int? BatchCount
```

and optional leaf fields:

```csharp
string? StableIdentityKind,
string? StableIdentityDigest
```

Accept schema 1 and 2; require the new fields only for schema 2. Sort releases by
payload digest then source release ID, keep a release whole, enforce default 250
releases and 4 GiB payload bytes, and write one checksummed package per batch.
Remove the 20–50 constraint only from the explicit full-batch writer path; retain
it for canary `export`.

Add:

```text
export-batches --master FILE --blob-root PATH --output DIR \
  --max-releases 250 --max-payload-bytes 4294967296
```

- [ ] **Step 4: Run package/partition tests**

Expected: schema v1 compatibility and strict v2 validation pass; batch output is
deterministic.

- [ ] **Step 5: Commit batch export**

```bash
git add backend/UsenetMigration/NzbDav/NzbDavExportManifest.cs \
  tools/NzbDavMigration/Export tools/NzbDavMigration/Recovery \
  tools/NzbDavMigration/Program.cs tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): export bounded full-library batches"
```

### Task 9: Persist master and batch provenance in the migration ledger

**Files:**
- Modify: `backend/Database/Models/UsenetMigration/UsenetMigrationEntities.cs`
- Modify: `backend/Database/UsenetMigrationDbContext.cs`
- Create: `backend/Database/UsenetMigrations/<timestamp>_AddNzbDavFullRecovery.cs`
- Create: `backend/Database/UsenetMigrations/<timestamp>_AddNzbDavFullRecovery.Designer.cs`
- Modify: `backend/Database/UsenetMigrations/UsenetMigrationDbContextModelSnapshot.cs`
- Create: `backend/UsenetMigration/Model/NzbDavBatchModels.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationSchemaTests.cs`

- [ ] **Step 1: Write failing additive-schema tests**

Assert a master run can own ordered batches and each batch stores package digest,
selection count, terminal status, plan digest, and applied/validated counts. Assert
unique `(MasterDigest, BatchIndex)` and package digest indexes.

- [ ] **Step 2: Run schema tests and confirm fields are absent**

Expected: compilation fails for `MigrationNzbDavMaster` and
`MigrationNzbDavBatch`.

- [ ] **Step 3: Add entities and generate an additive EF migration**

Use entities:

```csharp
public sealed class MigrationNzbDavMaster
{
    public long Id { get; set; }
    public string ManifestDigest { get; set; } = "";
    public int SourceLinkCount { get; set; }
    public int RecoverableCount { get; set; }
    public string Status { get; set; } = "planned";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class MigrationNzbDavBatch
{
    public long Id { get; set; }
    public long MasterId { get; set; }
    public int BatchIndex { get; set; }
    public string PackageDigest { get; set; } = "";
    public int SelectionCount { get; set; }
    public string Status { get; set; } = "pending";
    public long? RunId { get; set; }
    public string? PlanDigest { get; set; }
    public int AppliedCount { get; set; }
    public int ValidatedCount { get; set; }
}
```

Generate with:

```bash
cd backend
dotnet ef migrations add AddNzbDavFullRecovery --context UsenetMigrationDbContext \
  --output-dir Database/UsenetMigrations
```

Verify `Up` creates only new tables/indexes and `Down` drops only those tables.

- [ ] **Step 4: Run schema tests**

Expected: SQLite migration tests pass and existing migration rows remain readable.

- [ ] **Step 5: Commit the additive migration**

```bash
git add backend/Database backend/UsenetMigration/Model \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationSchemaTests.cs
git commit -m "feat(db): track NzbDav full-library batches"
```

The PR and deployment notes must say: back up `/config` before upgrading because
the additive migration applies automatically at startup.

### Task 10: Add batch connect/progress and strict sequencing APIs

**Files:**
- Modify: `backend/Api/Controllers/UsenetMigration/NzbDavMigrationController.cs`
- Modify: `backend/UsenetMigration/UsenetMigrationStore.cs`
- Modify: `backend/UsenetMigration/Runner/NzbDavScanRunner.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationControllerTests.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryEndToEndTests.cs`

- [ ] **Step 1: Write failing batch sequencing tests**

Connect a v2 batch, complete it, then connect the next. Assert the second is
rejected while the first has active submissions, non-exact rows, or no persisted
plan/apply acknowledgement. Assert reconnecting the same digest is idempotent and
does not duplicate submissions.

- [ ] **Step 2: Run controller/end-to-end tests**

Expected: batch fields are ignored and the second package is not fenced.

- [ ] **Step 3: Implement master registration and batch transitions**

Add endpoints:

```text
POST /api/migration/nzbdav/full/connect
GET  /api/migration/nzbdav/full/status
POST /api/migration/nzbdav/full/batches/{index}/acknowledge-plan
```

`full/connect` verifies package and master digests and projected coverage before
persisting the master/batch. It rejects out-of-order indices and a different
package at an existing index. Reuse existing scan/run controls for the active
batch; never create a second active batch. Status returns counts only, not source
metadata or article IDs.

- [ ] **Step 4: Run focused API/end-to-end tests**

Expected: legal sequencing passes; duplicate/out-of-order/unsafe transitions fail.

- [ ] **Step 5: Commit batch APIs**

```bash
git add backend/Api/Controllers/UsenetMigration backend/UsenetMigration \
  tests/NzbWebDAV.Tests/UsenetMigration
git commit -m "feat(migration): orchestrate bounded NzbDav batches"
```

### Task 11: Revalidate source links during create-only apply

**Files:**
- Modify: `tools/NzbDavMigration/Canary/CanaryLinkApplier.cs`
- Modify: `tools/NzbDavMigration/Canary/CanaryJournalModels.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryLinkApplierTests.cs`

- [ ] **Step 1: Write failing source-drift tests**

Create a source link beneath a temporary `/mnt/plex` analogue, generate a plan,
then change its target before apply. Assert no output link is created. Also test a
missing source link, unchanged source, existing unowned output, and a target-size
change between initial and immediate checks.

- [ ] **Step 2: Run applier tests and confirm source drift is not checked**

Expected: changed source links currently apply.

- [ ] **Step 3: Add explicit source root and no-follow revalidation**

Change the signature to:

```csharp
public Task<CanaryApplyJournal> ApplyAsync(
    string planPath,
    string sourceRoot,
    string libraryRoot,
    string targetRoot,
    string journalPath,
    CancellationToken cancellationToken = default)
```

Resolve `LibraryRelativePath` beneath both roots. Read the source link without
following it and require `LinkTarget == OriginalLegacyTarget` immediately before
creating the parallel link. Persist source root and observed target in the
journal. Add required CLI option `--source-root /mnt/plex`.

- [ ] **Step 4: Run applier tests**

Expected: unchanged sources apply idempotently; drift, conflict, and size races
fail before creation.

- [ ] **Step 5: Commit source fencing**

```bash
git add tools/NzbDavMigration/Canary tools/NzbDavMigration/Program.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryLinkApplierTests.cs
git commit -m "fix(migration): fence canary links against source drift"
```

### Task 12: Compute aggregate coverage and delta inventories

**Files:**
- Create: `tools/NzbDavMigration/Canary/CanaryCoverageReporter.cs`
- Modify: `tools/NzbDavMigration/Program.cs`
- Test: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryCoverageReporterTests.cs`

- [ ] **Step 1: Write failing denominator and drift tests**

Use initial and final snapshots with one added, one removed, one exact owned link,
one wrong target, and one missing link. Assert removed links leave the final
denominator, additions enter it, only journal-owned exact links enter the
numerator, and every remainder has one class.

- [ ] **Step 2: Run coverage tests and confirm the reporter is absent**

Expected: compilation fails for `CanaryCoverageReporter`.

- [ ] **Step 3: Implement the aggregate report and threshold exit**

Add:

```text
coverage-report --source-root /mnt/plex --library-root /mnt/plex2 \
  --initial-inventory FILE --master FILE --journals-dir DIR --output DIR \
  --minimum-coverage 0.90
```

The reporter inventories source and parallel trees without following links,
validates journal digests/ownership, stats InfiniDysk targets with bounded
timeouts, and writes create-new JSON/Markdown plus `SHA256SUMS`. Return exit code
3 below threshold and exit code 1 for structural or ownership errors.

- [ ] **Step 4: Run coverage tests**

Expected: numerator/denominator, drift, ownership, and classifications are exact.

- [ ] **Step 5: Commit coverage reporting**

```bash
git add tools/NzbDavMigration/Canary tools/NzbDavMigration/Program.cs \
  tests/NzbWebDAV.Tests/UsenetMigration/CanaryCoverageReporterTests.cs
git commit -m "feat(migration): report full-library link coverage"
```

### Task 13: Surface reconciliation and full-batch progress in the UI

**Files:**
- Modify: `frontend/app/clients/backend-client.server.ts`
- Modify: `frontend/app/routes/settings/migration/nzbdav/use-nzbdav-migration.ts`
- Modify: `frontend/app/routes/settings/migration/nzbdav/nzbdav-migration.tsx`
- Test: `frontend/app/routes/settings/migration/nzbdav/use-nzbdav-migration.test.ts`
- Test: `frontend/app/routes/settings/migration/nzbdav/nzbdav-migration.test.tsx`

- [ ] **Step 1: Write failing UI state tests**

Test that reconciliation is enabled only for terminal incomplete correlation,
that batch progress shows exact/applied/validated counts and coverage, and that
plan generation stays disabled unless selected equals exact and exclusions are
zero. Assert raw evidence/article fields are not rendered.

- [ ] **Step 2: Run focused Vitest**

Run:

```bash
cd frontend
npx vitest run app/routes/settings/migration/nzbdav/use-nzbdav-migration.test.ts \
  app/routes/settings/migration/nzbdav/nzbdav-migration.test.tsx
```

Expected: tests fail because reconciliation/full status is absent.

- [ ] **Step 3: Add typed client methods and controls**

Add server client methods for reconcile/full status and hook actions. Render
counts, percentage, terminal classifications, and the 90% dry-run gate. Keep
package/run digest confirmation and persistent warnings that `/mnt/plex2` is
host-applied and must remain outside Plex/Arr.

- [ ] **Step 4: Run focused Vitest and typecheck**

Run:

```bash
npx vitest run app/routes/settings/migration/nzbdav
npm run typecheck
```

Expected: focused tests and typecheck pass.

- [ ] **Step 5: Commit UI progress**

```bash
git add frontend/app/clients/backend-client.server.ts \
  frontend/app/routes/settings/migration/nzbdav
git commit -m "feat(ui): show full NzbDav recovery progress"
```

### Task 14: Complete end-to-end fixtures and operator documentation

**Files:**
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryEndToEndTests.cs`
- Create: `tests/NzbWebDAV.Tests/Fixtures/UsenetMigration/orphan-catalogue/`
- Modify: `docs/features/usenet-migration.md`
- Modify: `DEPLOYMENT-NOTES.md`
- Modify: `docs/superpowers/plans/2026-09-20-nzbdav-library-canary-migration.md`

- [ ] **Step 1: Add a failing end-to-end full-recovery fixture**

The fixture must include retained direct, orphan direct, eager archive, renamed
lazy archive, byte-identical duplicate payload, distinct ambiguity, corrupt NZB,
and source drift. Drive inventory → catalogue → recovery → batch export → backend
scan/submission stub → reconcile → plan → apply → validate → coverage.

Assert every source is classified, exact rows alone link, no source changes, and
coverage math uses the final snapshot.

- [ ] **Step 2: Run the end-to-end test**

Expected: it fails at the first missing full-recovery integration.

- [ ] **Step 3: Wire the completed units and document exact operations**

Document commands, private artifact permissions, backup warning, 90% preflight,
batch limits, pause/resume, plan/apply journals, delta pass, coverage report,
validation, rollback, and the prohibition on registering `/mnt/plex2` with Plex
or Arr. State that the setup wizard is unchanged because this is an advanced
migration workflow with no `ConfigKeys` addition.

- [ ] **Step 4: Run focused end-to-end and documentation checks not covered locally by CI**

Run the end-to-end test. Do not duplicate CI's full standard lanes locally; rely
on PR CI for backend/library/frontend/architecture/docs lanes unless a failure
needs diagnosis.

- [ ] **Step 5: Commit tests and docs**

```bash
git add tests/NzbWebDAV.Tests/UsenetMigration \
  tests/NzbWebDAV.Tests/Fixtures/UsenetMigration docs DEPLOYMENT-NOTES.md
git commit -m "chore(docs): document full NzbDav library recovery"
```

### Task 15: Verify, open the implementation PR, and obtain human merge

**Files:**
- Review all branch changes
- PR target: `johoja12/infinidysk:main`

- [ ] **Step 1: Run the verification-before-completion audit**

Inspect `git diff origin/main...HEAD`, confirm every spec acceptance criterion has
an implementing task and test, check no secrets/private production artifacts are
tracked, and run focused tests for all touched migration components. Confirm each
command's actual exit status and test counts.

- [ ] **Step 2: Push the exact branch and open one PR linked to issue #13**

Use a user-facing conventional title:

```text
feat(migration): recover the legacy NzbDav library into a parallel InfiniDysk mount
```

The PR body must include the `/config` backup warning, schema migration, safety
boundaries, test evidence, production rollout gates, and `Closes #13` only if the
full implementation—not merely the canary fix—is present.

- [ ] **Step 3: Verify remote head and CI**

Fetch the PR head SHA and compare it to local `HEAD`. Wait for required CI and
CodeQL checks; diagnose failures from authoritative logs and push focused commits
to the same branch.

- [ ] **Step 4: Stop for human merge**

Repository policy forbids agents from merging or enabling auto-merge. Report the
green PR and ask the human maintainer to merge it. After human merge, fetch
`origin/main` and verify the PR head is an ancestor of the remote merge commit.

### Task 16: Deploy and prove the 30-link canary

**Files:**
- Production host: nuc-1
- Compose project: `/opt/docker/infinidysk`
- Run artifacts: `/opt/infinidysk-migration/canary/<new-run-id>`

- [ ] **Step 1: Take fresh backups and immutable pre-deploy snapshots**

Back up InfiniDysk PostgreSQL/config, verify checksums, record current image and
commit marker, container restart/start times, both mount identities, queue/history
counts, `/mnt/plex` link snapshot, and `/mnt/plex2` state.

- [ ] **Step 2: Build/deploy only the merged commit**

Build a pinned local image from merged `origin/main`, update only the InfiniDysk
override, recreate only InfiniDysk, and wait for frontend/backend health. Do not
restart legacy NzbDav, either rclone, Plex, or Arr.

- [ ] **Step 3: Verify runtime acceptance**

Confirm image/commit markers, zero unexpected restarts, migrations, authenticated
API, LAN-only backend binding, read-only package mount, both FUSE mounts, and no
source link changes.

- [ ] **Step 4: Reconcile without resubmission**

Record queue/history/DAV counts before and after the reconcile endpoint. Require
30/30 exact, zero submissions, and unchanged counts except ledger timestamps.

- [ ] **Step 5: Generate/apply/validate the canary plan**

Require selected/exact/actionable all equal 30. Download and checksum the plan,
apply with explicit source/library/target roots, validate sizes and bounded reads,
and confirm `/mnt/plex` snapshot parity. Retain journal and reports.

### Task 17: Catalogue, recover, and gate the full production run

**Files:**
- Production host: nuc-1
- Private run root: `/opt/infinidysk-migration/full/<run-id>`

- [ ] **Step 1: Capture the initial source and blob inventories**

Use create-new private artifacts, checksum them, and record the exact source link
denominator, blob count/bytes, disk free space, and legacy/InfiniDysk health.

- [ ] **Step 2: Build and seal the orphan catalogue**

Run the resumable scanner with bounded monitoring. Repeatedly sample progress,
disk usage, legacy service latency, and errors. Pause rather than overload the
host. Seal only after the frozen input is completely classified.

- [ ] **Step 3: Run deterministic recovery**

Produce master/recovery/exclusion reports. Independently verify totals sum to the
source denominator and inspect ambiguity/corrupt/missing samples without exposing
raw metadata.

- [ ] **Step 4: Enforce the projected 90% gate**

If recoverable/source is below 0.90, stop before package export or import and
report classifications. If it is at least 0.90, checksum the approval artifact
and generate deterministic batch manifests.

### Task 18: Import all approved batches and build `/mnt/plex2`

**Files:**
- Production host: nuc-1
- Source root: `/mnt/plex`
- Parallel root: `/mnt/plex2`
- Target root: `/mnt/remote/infinidysk`

- [ ] **Step 1: Process batches sequentially**

For each index: stage/verify package, connect, map only dedicated migration
categories, scan, review zero blocking verdicts, run with one worker and queue
depth five, monitor to terminal, reconcile to all exact, generate/checksum plan,
apply create-only links, validate, acknowledge, then advance. Never restart a
batch solely because a monitoring call times out.

- [ ] **Step 2: Reconcile failures without unsafe links**

Pause on failed submissions, non-exact rows, plan count mismatch, target-size
mismatch, source drift, or apply conflict. Preserve artifacts; repair and resume
the same batch. Never create filename-only, legacy-target, or unjournaled links.

- [ ] **Step 3: Run the final delta pass**

Take a fresh `/mnt/plex` inventory, catalogue new blob inputs, recover/import new
or changed links through additional deterministic batches, and record source
deletions separately without deleting parallel links.

- [ ] **Step 4: Prove final coverage and filesystem invariants**

Run aggregate coverage against the fresh final snapshot. Require at least 90%
owned, exact-target, size-validated links. Confirm every `/mnt/plex2` symlink
target begins `/mnt/remote/infinidysk/.ids/`, no paths target NzbDav, every
remainder has one exclusion class, and `/mnt/plex` was unchanged by tooling.

- [ ] **Step 5: Run bounded performance and media validation**

Select reviewed direct/eager/lazy, TV/movie/4K, small/large cases. Run first and
repeat sequential throughput, seeks at 10/50/90%, bounded 8 MiB reads, and
representative `ffprobe`. Retain JSON/Markdown reports with route/cache labels.

- [ ] **Step 6: Leave consumers untouched and publish the operator result**

Keep `/mnt/plex2` unregistered. Report source/final counts, exact percentage,
batch totals, exclusions, validation/performance results, container/mount health,
and retained rollback artifacts. Plex/Arr cutover requires a new explicit request.
