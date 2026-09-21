# Preserve NzbDav Package Digest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve verified NzbDav package provenance through completion so strict reconciliation can safely process completed imports.

**Architecture:** Add one focused JSON evidence helper that requires an existing lowercase SHA-256 package digest, preserves the scan-time evidence document, and nests the latest correlation evidence. Use it only in the NzbDav completion path; keep reconciliation's digest gate unchanged and repair the historical production canary separately from independently retained evidence.

**Tech Stack:** .NET 10, System.Text.Json.Nodes, EF Core/SQLite, xUnit, Docker Compose, curl, sqlite3.

---

## File map

- Create `backend/UsenetMigration/Provenance/NzbDavLedgerEvidence.cs` to own strict JSON evidence merging.
- Modify `backend/UsenetMigration/Provenance/MigrationProvenanceService.cs` to preserve scan-time NzbDav evidence.
- Create `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavLedgerEvidenceTests.cs` for merge and fail-closed behavior.
- Modify `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavReconciliationServiceTests.cs` to pin missing/mismatched digest rejection.

### Task 1: Preserve package provenance during completion

**Files:**
- Create: `backend/UsenetMigration/Provenance/NzbDavLedgerEvidence.cs`
- Modify: `backend/UsenetMigration/Provenance/MigrationProvenanceService.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavLedgerEvidenceTests.cs`

- [ ] **Step 1: Write failing evidence-merge tests**

Add tests that pass an existing flags object containing `packageSha256` and
`payloadLength`, plus a correlation evidence object. Assert the result preserves
both original properties and adds `correlation.match`. Add theories asserting
that malformed JSON, a non-object document, a missing digest, an invalid digest,
or non-object correlation evidence throws `InvalidDataException`.

```csharp
var digest = new string('a', 64);
var merged = NzbDavLedgerEvidence.WithCorrelation(
    $$"""{"packageSha256":"{{digest}}","payloadLength":42}""",
    """{"match":"article-identity"}""");
using var document = JsonDocument.Parse(merged);
Assert.Equal(digest, document.RootElement.GetProperty("packageSha256").GetString());
Assert.Equal(42, document.RootElement.GetProperty("payloadLength").GetInt32());
Assert.Equal("article-identity",
    document.RootElement.GetProperty("correlation").GetProperty("match").GetString());
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzbDavLedgerEvidenceTests'
```

Expected: compilation fails because `NzbDavLedgerEvidence` does not exist.

- [ ] **Step 3: Implement strict evidence merging**

Create an internal static helper using `JsonNode.Parse`. Require both inputs to
be `JsonObject`; require `packageSha256` to be exactly 64 lowercase hexadecimal
characters; clone the correlation object into `existing["correlation"]`; serialize
the resulting object. Wrap `JsonException` as `InvalidDataException` without
including evidence contents in the message.

Change only the NzbDav branch in `RecordCompletedAsync`:

```csharp
sourceFile.FileStatus = result.Status;
sourceFile.Flags = NzbDavLedgerEvidence.WithCorrelation(sourceFile.Flags, result.Evidence);
sourceFile.NewDavItemId = result.DavItemId?.ToString();
```

- [ ] **Step 4: Run the focused tests**

Expected: all evidence tests pass.

- [ ] **Step 5: Commit the behavior fix**

```bash
git add backend/UsenetMigration/Provenance \
  tests/NzbWebDAV.Tests/UsenetMigration/NzbDavLedgerEvidenceTests.cs
git commit -m "fix(migration): preserve NzbDav package provenance"
```

### Task 2: Pin reconciliation's strict digest gate

**Files:**
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavReconciliationServiceTests.cs`

- [ ] **Step 1: Add missing and mismatched digest tests**

Use the existing `SeedAsync` harness. In one theory, replace all selected
`ReleaseFiles.Flags` values with correlation-only evidence; in the other, set a
different 64-character digest. Assert `ReconcileAsync` throws
`InvalidDataException`, and assert queue/history/DAV counts and mapped targets
remain unchanged.

```csharp
await Assert.ThrowsAsync<InvalidDataException>(() =>
    service.ReconcileAsync(fixture.RunId, fixture.PackageRoot));
```

- [ ] **Step 2: Run reconciliation and evidence tests**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzbDavLedgerEvidenceTests|FullyQualifiedName~NzbDavReconciliationServiceTests'
```

Expected: all tests pass, including the existing idempotence and zero-write test.

- [ ] **Step 3: Commit the safety regression tests**

```bash
git add tests/NzbWebDAV.Tests/UsenetMigration/NzbDavReconciliationServiceTests.cs
git commit -m "chore(test): keep NzbDav reconciliation digest strict"
```

### Task 3: Verify and open the repair PR

**Files:**
- Review all branch changes
- PR target: `johoja12/infinidysk:main`

- [ ] **Step 1: Run focused verification**

Run the two focused test classes with a fresh Release build, run
`git diff --check origin/main...HEAD`, inspect the diff for secret or production
artifact leakage, and confirm the only application change is evidence merging.

- [ ] **Step 2: Push and open the PR**

Push `fix/migration-preserve-package-digest` and open a PR titled:

```text
fix(migration): completed NzbDav imports retain package verification
```

The PR body must describe the production fail-closed observation, state that no
queue/history/DAV or symlink writes occurred, and include focused test evidence.

- [ ] **Step 3: Wait for required CI and obtain explicit merge authorization**

Verify the PR head SHA and required checks. Do not infer authorization to merge
this new PR from the earlier authorization for PR #19.

### Task 4: Deploy and repair the historical canary ledger

**Files:**
- Production host: `192.168.20.65` (`nuc-1`)
- Existing run root: `/opt/infinidysk-migration/full/20260921T162300Z-b2bc83f5`

- [ ] **Step 1: Back up and verify the live ledger**

After human-authorized merge, build and deploy only the merged commit. Stop if
health, revision, mounts, binding, or source snapshot checks fail. Copy
`/opt/infinidysk/config/usenet-migration.db` plus any WAL/SHM files into a new
root-only pre-repair directory and write/verify `SHA256SUMS`.

- [ ] **Step 2: Revalidate independent digest evidence**

Require the SHA-256 of the frozen package's `SHA256SUMS` to equal the unique
`packageDigest` in both retained files:

```text
/opt/infinidysk-migration/canary/phase-b-20260920T184834Z/connect-response.json
/opt/infinidysk-migration/canary/phase-b-20260920T184834Z/correlation-report.json
```

Validate all package checksums before touching the ledger.

- [ ] **Step 3: Repair exactly 30 rows in one transaction**

Use `sqlite3` with `BEGIN IMMEDIATE`. First require 30 selected rows, zero rows
already carrying a package digest, and valid JSON on every row. Update only those
rows belonging to current run 1 and selected package source IDs, using
`json_set(Flags, '$.packageSha256', :digest)`. Require `changes() == 30`, verify
all 30 now match the expected digest, then commit; otherwise roll back.

- [ ] **Step 4: Retry reconciliation with zero-write assertions**

Record queue/history/DAV counts, call reconciliation once, and require 30 selected,
30 exact, zero ambiguous, zero unmatched, and zero submitted. Re-record counts and
require exact equality. Retain the response and checksums under the private run
root.

- [ ] **Step 5: Resume canary plan/apply only after reconciliation passes**

Generate the exact-only plan, require 30 planned rows, apply create-only with
`/mnt/plex` as source fence and `/mnt/remote/infinidysk` as target root, validate
bounded reads and sizes, and confirm the source snapshot remains unchanged.
