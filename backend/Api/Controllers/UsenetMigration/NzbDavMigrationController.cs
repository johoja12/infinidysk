using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Canary;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Provenance;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.UsenetMigration.Triage;

namespace NzbWebDAV.Api.Controllers.UsenetMigration;

public sealed class NzbDavMigrationController(
    UsenetMigrationStore store,
    UsenetMigrationRunner runner,
    NzbDavReconciliationService? reconciliation = null) : UsenetMigrationBaseController
{
    private readonly NzbDavPackageReader _packageReader = new();
    private readonly Action _interruptScan = () => runner?.InterruptScan();
    private readonly Action _interruptSubmissions = () => runner?.InterruptSubmissionBatch();
    private readonly NzbDavReconciliationService _reconciliation = reconciliation
        ?? new NzbDavReconciliationService(store, new NzbDavPackageReader(), BlobStore.Current);

    [HttpPost("api/migration/nzbdav/connect")]
    public Task<IActionResult> Connect([FromBody] NzbDavConnectRequest request) => GuardedAsync(async () =>
    {
        if (request is null)
            throw new BadHttpRequestException("Request body is required.");
        var packagePath = RequirePackagePath(request.PackagePath);
        var session = await store.GetSessionAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (!UsenetMigrationController.CanConnect(session.Status))
            throw new BadHttpRequestException(
                $"Cannot connect while migration operation '{session.Status}' is active.");
        var package = await ReadPackageAsync(packagePath).ConfigureAwait(false);
        var categories = package.Manifest.SelectedLinks
            .Select(link => link.LibraryRelativePath.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var transition = await store.ApplyNzbDavConnectionAsync(
            new NzbDavMigrationConnectionValues(
                package.RootPath,
                request.MaxQueueDepth ?? 5,
                request.SubmitWorkers ?? 1,
                categories),
            HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException(
                $"Cannot connect while migration operation '{transition.CurrentStatus}' is active.");
        return Ok(new
        {
            status = true,
            state = "connected",
            packageDigest = package.PackageDigest,
            selectionCount = package.Manifest.SelectedLinks.Count,
            exclusionCount = package.Manifest.Releases.SelectMany(release => release.Leaves)
                .Count(leaf => leaf.ExtractionStatus != "ready" || leaf.ExclusionReason is not null),
            releaseCount = package.Manifest.Releases.Count,
            categories,
            maxQueueDepth = request.MaxQueueDepth ?? 5,
            submitWorkers = request.SubmitWorkers ?? 1,
        });
    });

    [HttpPost("api/migration/nzbdav/full/connect")]
    public Task<IActionResult> ConnectFull([FromBody] NzbDavFullConnectRequest request) => GuardedAsync(async () =>
    {
        if (request is null)
            throw new BadHttpRequestException("Request body is required.");
        var packagePath = RequirePackagePath(request.PackagePath);
        var package = await ReadPackageAsync(packagePath).ConfigureAwait(false);
        if (package.Manifest.SchemaVersion != NzbDavExportManifest.CurrentSchemaVersion
            || package.Manifest.MasterManifestDigest is null
            || package.Manifest.BatchIndex is null)
            throw new BadHttpRequestException("Full-library connect requires a schema-v2 batch package.");
        if (!string.Equals(package.Manifest.MasterManifestDigest, request.MasterManifestDigest,
                StringComparison.Ordinal))
            throw new BadHttpRequestException("The package master manifest digest does not match the request.");
        if (request.SourceLinkCount <= 0
            || request.RecoverableCount < 0
            || request.RecoverableCount > request.SourceLinkCount)
            throw new BadHttpRequestException("Projected source and recoverable counts are invalid.");
        var coverage = (double)request.RecoverableCount / request.SourceLinkCount;
        if (package.Manifest.SelectedLinks.Count == 0
            || package.Manifest.SelectedLinks.Count > request.RecoverableCount)
            throw new BadHttpRequestException("The batch selection count is invalid for this recovery master.");

        var categories = package.Manifest.SelectedLinks
            .Select(link => link.LibraryRelativePath.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        NzbDavFullConnectionResult result;
        try
        {
            result = await store.ApplyNzbDavFullConnectionAsync(
                new NzbDavFullConnectionValues(
                    package.RootPath,
                    request.MasterManifestDigest,
                    package.PackageDigest,
                    package.Manifest.BatchIndex.Value,
                    package.Manifest.SelectedLinks.Count,
                    request.SourceLinkCount,
                    request.RecoverableCount,
                    request.MaxQueueDepth ?? 5,
                    request.SubmitWorkers ?? 1,
                    categories),
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new BadHttpRequestException(exception.Message, exception);
        }
        return Ok(new
        {
            status = true,
            state = result.Batch.Status,
            batchIndex = result.Batch.BatchIndex,
            batchCount = package.Manifest.BatchCount,
            selectionCount = result.Batch.SelectionCount,
            sourceLinkCount = result.Master.SourceLinkCount,
            recoverableCount = result.Master.RecoverableCount,
            coverage,
            alreadyRegistered = result.AlreadyRegistered,
        });
    });

    [HttpGet("api/migration/nzbdav/full/status")]
    public Task<IActionResult> GetFullStatus() => GuardedAsync(async () =>
    {
        var master = await store.GetNzbDavFullStatusAsync(HttpContext.RequestAborted).ConfigureAwait(false)
                     ?? throw new BadHttpRequestException("No full-library recovery is registered.");
        return Ok(new
        {
            status = true,
            recoveryStatus = master.Status,
            sourceLinkCount = master.SourceLinkCount,
            recoverableCount = master.RecoverableCount,
            coverage = master.SourceLinkCount == 0
                ? 0
                : (double)master.RecoverableCount / master.SourceLinkCount,
            batchCount = master.Batches.Count,
            selectedCount = master.Batches.Sum(batch => batch.SelectionCount),
            appliedCount = master.Batches.Sum(batch => batch.AppliedCount),
            validatedCount = master.Batches.Sum(batch => batch.ValidatedCount),
            batches = master.Batches.Select(batch => new
            {
                batchIndex = batch.BatchIndex,
                selectionCount = batch.SelectionCount,
                status = batch.Status,
                appliedCount = batch.AppliedCount,
                validatedCount = batch.ValidatedCount,
            }),
        });
    });

    [HttpPost("api/migration/nzbdav/full/batches/{index:int}/acknowledge-plan")]
    public Task<IActionResult> AcknowledgePlan(
        int index,
        [FromBody] NzbDavBatchPlanAcknowledgementRequest request) => GuardedAsync(async () =>
    {
        if (request is null || !IsSha256(request.PlanDigest))
            throw new BadHttpRequestException("A lowercase SHA-256 plan digest is required.");
        if (request.AppliedCount < 0 || request.ValidatedCount < 0)
            throw new BadHttpRequestException("Applied and validated counts cannot be negative.");
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        var package = await ReadPackageAsync(session.SourcePackageRoot!).ConfigureAwait(false);
        if (package.Manifest.SchemaVersion != NzbDavExportManifest.CurrentSchemaVersion
            || package.Manifest.MasterManifestDigest is null
            || package.Manifest.BatchIndex != index)
            throw new BadHttpRequestException("The active package does not match this full-library batch.");
        var selectedIds = package.Manifest.SelectedLinks
            .Select(link => link.LegacyDavItemId.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            var batch = await store.AcknowledgeNzbDavBatchAsync(
                package.Manifest.MasterManifestDigest,
                index,
                package.PackageDigest,
                selectedIds,
                request.PlanDigest!,
                request.AppliedCount,
                request.ValidatedCount,
                HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new
            {
                status = true,
                batchIndex = batch.BatchIndex,
                state = batch.Status,
                selectionCount = batch.SelectionCount,
                appliedCount = batch.AppliedCount,
                validatedCount = batch.ValidatedCount,
            });
        }
        catch (InvalidOperationException exception)
        {
            throw new BadHttpRequestException(exception.Message, exception);
        }
    });

    [HttpGet("api/migration/nzbdav/categories")]
    public Task<IActionResult> GetCategories() => GuardedAsync(async () =>
    {
        await RequireNzbDavSessionAsync().ConfigureAwait(false);
        var categories = await store.GetCategoryMapAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { status = true, categories });
    });

    [HttpPut("api/migration/nzbdav/categories")]
    public Task<IActionResult> PutCategories([FromBody] CategoryMapRequest request) => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (!UsenetMigrationController.CanEditCategoryMappings(session.Status))
            throw new BadHttpRequestException("Category mappings can only be changed before migration work starts.");
        if (request.Mappings is null || request.Mappings.Count == 0)
            throw new BadHttpRequestException("At least one mapping is required.");
        var changes = request.Mappings.Select(mapping => new MigrationCategoryMappingChange(
            mapping.AltmountCategory,
            mapping.TargetCategory,
            mapping.Action == "exclude" ? "exclude" : "migrate")).ToArray();
        var transition = await store.ApplyCategoryMappingsAsync(changes, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException(
                $"Category mappings cannot change while migration operation '{transition.CurrentStatus}' is active.");
        return Ok(new
        {
            status = true,
            categories = await store.GetCategoryMapAsync(HttpContext.RequestAborted).ConfigureAwait(false),
        });
    });

    [HttpPost("api/migration/nzbdav/scan")]
    public Task<IActionResult> StartScan() => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.Status == "scanning")
            return Ok(new { status = true, state = "scanning" });
        if (session.SourcePackageRoot is null || !UsenetMigrationController.CanStartScan(session.Status))
            throw new BadHttpRequestException(
                $"Cannot scan while migration operation '{session.Status}' is active.");
        await ReadPackageAsync(session.SourcePackageRoot).ConfigureAwait(false);
        var transition = await store.TryTransitionSessionAsync(
            MigrationSessionTransition.StartScan, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException(
                $"Cannot scan while migration operation '{transition.CurrentStatus}' is active.");
        return Ok(new { status = true, state = "scanning" });
    });

    [HttpDelete("api/migration/nzbdav/scan")]
    public Task<IActionResult> CancelScan() => GuardedAsync(async () =>
    {
        await RequireNzbDavSessionAsync().ConfigureAwait(false);
        var transition = await store.TryTransitionSessionAsync(
            MigrationSessionTransition.CancelScan, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException(
                $"Only an active scan can be cancelled; current state is '{transition.CurrentStatus}'.");
        _interruptScan();
        return Ok(new { status = true, state = "scan_cancelling" });
    });

    [HttpPost("api/migration/nzbdav/run")]
    public Task<IActionResult> StartRun([FromBody] NzbDavRunRequest request) => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.Status == "running")
            return Ok(new { status = true, state = "running" });
        if (!UsenetMigrationController.CanStartMigration(session.Status))
            throw new BadHttpRequestException("Complete a new NzbDav package scan before starting the import.");
        var package = await ReadPackageAsync(session.SourcePackageRoot!).ConfigureAwait(false);
        if (request is null
            || request.PackageDigest != package.PackageDigest
            || request.SelectionCount != package.Manifest.SelectedLinks.Count)
            throw new BadHttpRequestException(
                "Run confirmation must match the immutable package digest and exact selection count.");
        await using (var context = store.NewContext())
        {
            var blocking = await context.Releases.AsNoTracking().CountAsync(
                release => release.Included &&
                           (release.VerdictReasons.Contains(VerdictReason.QueueKeyCollision)
                            || release.VerdictReasons.Contains(VerdictReason.CollidesWithExistingQueueItem)
                            || release.VerdictReasons.Contains(VerdictReason.CategoryUnmapped)),
                HttpContext.RequestAborted).ConfigureAwait(false);
            if (blocking > 0)
                return Conflict(new BaseApiResponse
                {
                    Status = false,
                    Error = $"{blocking} included release(s) have blocking collisions or unmapped categories.",
                });
        }
        var transition = await store.TryTransitionSessionAsync(
            MigrationSessionTransition.StartRun, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException(
                $"Cannot start while migration operation '{transition.CurrentStatus}' is active.");
        var runId = await store.BeginRunAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (package.Manifest.SchemaVersion == NzbDavExportManifest.CurrentSchemaVersion)
            await store.AttachNzbDavBatchRunAsync(package.PackageDigest, runId, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        return Ok(new { status = true, state = "running" });
    });

    [HttpPost("api/migration/nzbdav/run/resume")]
    public Task<IActionResult> ResumeRun() => GuardedAsync(async () =>
    {
        await RequireNzbDavSessionAsync().ConfigureAwait(false);
        var transition = await store.TryTransitionSessionAsync(
            MigrationSessionTransition.ResumeRun, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException("Only a paused NzbDav import can be resumed.");
        return Ok(new { status = true, state = "running" });
    });

    [HttpDelete("api/migration/nzbdav/run")]
    public Task<IActionResult> StopRun([FromQuery] bool cancel = false) => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (cancel)
        {
            if (!UsenetMigrationController.CanCancelMigration(session.Status))
                throw new BadHttpRequestException("Only a running or paused NzbDav import can be cancelled.");
            var state = await store.BeginCancellationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            _interruptSubmissions();
            return Ok(new { status = true, state });
        }
        var transition = await store.TryTransitionSessionAsync(
            MigrationSessionTransition.PauseRun, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!transition.Succeeded)
            throw new BadHttpRequestException("Only a running NzbDav import can be paused.");
        _interruptSubmissions();
        return Ok(new { status = true, state = "paused" });
    });

    [HttpGet("api/migration/nzbdav/status")]
    public Task<IActionResult> GetStatus() => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        await using var context = store.NewContext();
        var submissions = await context.Submissions.AsNoTracking()
            .GroupBy(item => item.State)
            .Select(group => new { state = group.Key, count = group.Count() })
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            sessionStatus = session.Status,
            sourcePackageRoot = session.SourcePackageRoot,
            canaryLibraryRoot = session.CanaryLibraryRoot,
            session.MaxQueueDepth,
            session.SubmitWorkers,
            submissions,
        });
    });

    [HttpGet("api/migration/nzbdav/correlation")]
    public Task<IActionResult> GetCorrelation() => GuardedAsync(async () =>
    {
        var report = await BuildCorrelationReportAsync().ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            packageDigest = report.PackageDigest,
            selectedCount = report.SelectedCount,
            exclusionCount = report.ExclusionCount,
            ambiguityCount = report.AmbiguityCount,
            exactCount = report.ExactCount,
            rows = report.Rows,
        });
    });

    [HttpGet("api/migration/nzbdav/import-failures")]
    public Task<IActionResult> GetImportFailures() => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.Status != "complete")
            throw new BadHttpRequestException("Wait for the import to reach a terminal state.");
        var package = await ReadPackageAsync(session.SourcePackageRoot!).ConfigureAwait(false);
        var failures = await LoadTerminalFailuresAsync(package).ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            packageDigest = package.PackageDigest,
            batchIndex = package.Manifest.BatchIndex,
            failedCount = failures.Count,
            failures,
        });
    });

    [HttpPost("api/migration/nzbdav/reconcile")]
    public Task<IActionResult> Reconcile() => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.CurrentRunId is null || session.SourcePackageRoot is null)
            throw new BadHttpRequestException("No completed NzbDav run is available to reconcile.");
        var result = await _reconciliation.ReconcileAsync(
            session.CurrentRunId.Value,
            session.SourcePackageRoot,
            HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            result.RunId,
            result.SelectedCount,
            result.ExactCount,
            result.AmbiguousCount,
            result.UnmatchedCount,
            result.SubmittedCount,
        });
    });

    [HttpPost("api/migration/nzbdav/canary-plan")]
    public Task<IActionResult> GenerateCanaryPlan() => GuardedAsync(async () =>
    {
        var report = await BuildCorrelationReportAsync().ConfigureAwait(false);
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.CurrentRunId is null)
            throw new BadHttpRequestException("The completed import has no migration run identity.");
        var package = await ReadPackageAsync(session.SourcePackageRoot!).ConfigureAwait(false);
        var failures = package.Manifest.SchemaVersion == NzbDavExportManifest.CurrentSchemaVersion
            ? await LoadTerminalFailuresAsync(package).ConfigureAwait(false)
            : [];
        var failedIds = failures.Select(failure => failure.LegacyDavItemId).ToHashSet();
        if (report.ExclusionCount != 0 || report.AmbiguityCount != 0
            || report.ExactCount + failedIds.Count != report.SelectedCount
            || report.Rows.Any(row => row.CorrelationStatus != "exact"
                                      && (!failedIds.Contains(row.LegacyDavItemId)
                                          || row.CorrelationStatus != "import-failed")))
            throw new BadHttpRequestException(
                "Plans require an exact correlation or recorded terminal import failure for every selected link "
                + $"(selected: {report.SelectedCount}, exact: {report.ExactCount}, "
                + $"failed: {failedIds.Count}, excluded: {report.ExclusionCount}, "
                + $"ambiguous: {report.AmbiguityCount}).");
        var planDirectory = NzbDavCanaryPlanWriter.GetPlanDirectory(
            Path.Join(DavDatabaseContext.ConfigPath, "migration-output", "nzbdav"),
            session.CurrentRunId.Value,
            report.PackageDigest);
        if (Directory.Exists(planDirectory))
            return Conflict(new BaseApiResponse { Status = false, Error = "The immutable canary plan already exists." });
        await using var context = store.NewContext();
        var result = await new NzbDavCanaryLinkPlanner().GenerateAsync(
            context, session.SourcePackageRoot!, session.CurrentRunId.Value, failedIds, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            result.Plan.RunId,
            result.Plan.SourcePackageDigest,
            result.Plan.SelectedCount,
            result.Plan.ActionableCount,
            failedCount = failedIds.Count,
            download = "/api/migration/nzbdav/canary-plan",
        });
    });

    private async Task<IReadOnlyList<NzbDavImportFailure>> LoadTerminalFailuresAsync(NzbDavVerifiedPackage package)
    {
        await using var context = store.NewContext();
        var failedSubmissions = await context.Submissions.AsNoTracking()
            .Where(item => item.State == "failed" || item.State == "evicted")
            .ToDictionaryAsync(item => item.StoreRef, HttpContext.RequestAborted).ConfigureAwait(false);
        var releaseById = package.Manifest.Releases
            .SelectMany(release => release.Leaves.Select(leaf => (leaf.LegacyDavItemId, release.SourceReleaseId)))
            .ToDictionary(item => item.LegacyDavItemId, item => item.SourceReleaseId);
        return package.Manifest.SelectedLinks
            .Where(link => failedSubmissions.ContainsKey($"nzbdav:{releaseById[link.LegacyDavItemId]}"))
            .Select(link =>
            {
                var releaseId = releaseById[link.LegacyDavItemId];
                var submission = failedSubmissions[$"nzbdav:{releaseId}"];
                return new NzbDavImportFailure(releaseId, link.LegacyDavItemId,
                    link.LibraryRelativePath, submission.State, submission.Error);
            })
            .OrderBy(item => item.LibraryRelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    [HttpGet("api/migration/nzbdav/canary-plan")]
    public Task<IActionResult> DownloadCanaryPlan() => GuardedAsync(async () =>
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.CurrentRunId is null || session.SourcePackageRoot is null)
            throw new BadHttpRequestException("No NzbDav canary plan is available.");
        var package = await ReadPackageAsync(session.SourcePackageRoot).ConfigureAwait(false);
        var directory = NzbDavCanaryPlanWriter.GetPlanDirectory(
            Path.Join(DavDatabaseContext.ConfigPath, "migration-output", "nzbdav"),
            session.CurrentRunId.Value,
            package.PackageDigest);
        var planPath = Path.Join(directory, "plan.json");
        var checksumsPath = Path.Join(directory, "SHA256SUMS");
        if (!System.IO.File.Exists(planPath) || !System.IO.File.Exists(checksumsPath))
            throw new BadHttpRequestException("No NzbDav canary plan is available.");

        await using var bundle = new MemoryStream();
        using (var archive = new ZipArchive(bundle, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in new[] { planPath, checksumsPath })
            {
                var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.NoCompression);
                await using var output = await entry.OpenAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                await using var input = System.IO.File.OpenRead(path);
                await input.CopyToAsync(output, HttpContext.RequestAborted).ConfigureAwait(false);
            }
        }
        return File(
            bundle.ToArray(),
            "application/zip",
            $"nzbdav-canary-plan-run-{session.CurrentRunId.Value}.zip");
    });

    private async Task<MigrationSessionState> RequireNzbDavSessionAsync()
    {
        var session = await store.GetSessionAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (session.SourceType != MigrationSourceTypes.NzbDav || session.SourcePackageRoot is null)
            throw new BadHttpRequestException("Connect a verified NzbDav export package first.");
        return session;
    }

    private async Task<NzbDavVerifiedPackage> ReadPackageAsync(string packagePath)
    {
        try
        {
            return await _packageReader.ReadAsync(packagePath, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BadHttpRequestException($"NzbDav export package validation failed: {exception.Message}", exception);
        }
    }

    private async Task<NzbDavCorrelationReport> BuildCorrelationReportAsync()
    {
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.Status != "complete")
            throw new BadHttpRequestException("Finish terminal import reconciliation before viewing correlation.");
        await using var context = store.NewContext();
        var active = await context.Submissions.AsNoTracking().CountAsync(
            submission => submission.State == "pending"
                          || submission.State == "submitting"
                          || submission.State == "submitted"
                          || submission.State == "processing",
            HttpContext.RequestAborted).ConfigureAwait(false);
        if (active != 0)
            throw new BadHttpRequestException("Import reconciliation is not terminal yet.");
        var package = await ReadPackageAsync(session.SourcePackageRoot!).ConfigureAwait(false);
        var files = await context.ReleaseFiles.AsNoTracking()
            .Where(file => file.SourceFileId != null)
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var byId = files.GroupBy(file => file.SourceFileId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var leaves = package.Manifest.Releases.SelectMany(release => release.Leaves)
            .ToDictionary(leaf => leaf.LegacyDavItemId);
        var rows = package.Manifest.SelectedLinks.Select(link =>
        {
            byId.TryGetValue(link.LegacyDavItemId.ToString(), out var file);
            var leaf = leaves[link.LegacyDavItemId];
            return new NzbDavCorrelationRow(
                link.LibraryRelativePath,
                link.LegacyDavItemId,
                leaf.FileSize,
                leaf.ExtractionStatus,
                leaf.ExclusionReason,
                file?.FileStatus ?? "not-imported",
                file?.NewDavItemId,
                file?.Flags ?? "{}");
        }).ToArray();
        return new NzbDavCorrelationReport(
            package.PackageDigest,
            rows.Length,
            rows.Count(row => row.ExtractionStatus != "ready" || row.ExclusionReason is not null),
            rows.Count(row => row.CorrelationStatus is "ambiguous" or "duplicate"),
            rows.Count(row => row.CorrelationStatus == "exact"),
            rows);
    }

    private static string RequirePackagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BadHttpRequestException("packagePath is required.");
        var inputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Join(DavDatabaseContext.ConfigPath, "migration-input")));
        var inputInfo = new DirectoryInfo(inputRoot);
        if (!inputInfo.Exists)
            throw new BadHttpRequestException("CONFIG_PATH/migration-input is not mounted.");
        if (inputInfo.LinkTarget is not null || inputInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new BadHttpRequestException("CONFIG_PATH/migration-input cannot be a symbolic link.");
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!candidate.StartsWith(inputRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BadHttpRequestException("packagePath must be beneath CONFIG_PATH/migration-input.");
        var relative = Path.GetRelativePath(inputRoot, candidate);
        var current = inputRoot;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Join(current, component);
            var info = new DirectoryInfo(current);
            if (!info.Exists)
                throw new BadHttpRequestException($"packagePath directory does not exist: {candidate}");
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new BadHttpRequestException("packagePath cannot traverse symbolic links.");
        }
        return candidate;
    }

    private static bool IsSha256(string? digest) =>
        digest is { Length: 64 }
        && digest.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

public sealed record NzbDavConnectRequest(string? PackagePath, int? MaxQueueDepth, int? SubmitWorkers);
public sealed record NzbDavFullConnectRequest(
    string? PackagePath,
    string MasterManifestDigest,
    int SourceLinkCount,
    int RecoverableCount,
    int? MaxQueueDepth,
    int? SubmitWorkers);
public sealed record NzbDavBatchPlanAcknowledgementRequest(
    string? PlanDigest,
    int AppliedCount,
    int ValidatedCount);
public sealed record NzbDavRunRequest(string? PackageDigest, int? SelectionCount);
public sealed record NzbDavImportFailure(
    string SourceReleaseId,
    Guid LegacyDavItemId,
    string LibraryRelativePath,
    string SubmissionState,
    string? Reason);
public sealed record NzbDavCorrelationRow(
    string LibraryRelativePath,
    Guid LegacyDavItemId,
    long ExpectedFileSize,
    string ExtractionStatus,
    string? ExclusionReason,
    string CorrelationStatus,
    string? InfiniDyskDavItemId,
    string CorrelationEvidence);
public sealed record NzbDavCorrelationReport(
    string PackageDigest,
    int SelectedCount,
    int ExclusionCount,
    int AmbiguityCount,
    int ExactCount,
    IReadOnlyList<NzbDavCorrelationRow> Rows);
