using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Canary;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.UsenetMigration.Triage;

namespace NzbWebDAV.Api.Controllers.UsenetMigration;

public sealed class NzbDavMigrationController(
    UsenetMigrationStore store,
    UsenetMigrationRunner runner) : UsenetMigrationBaseController
{
    private readonly NzbDavPackageReader _packageReader = new();
    private readonly Action _interruptScan = () => runner?.InterruptScan();
    private readonly Action _interruptSubmissions = () => runner?.InterruptSubmissionBatch();

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
        await store.BeginRunAsync(HttpContext.RequestAborted).ConfigureAwait(false);
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

    [HttpPost("api/migration/nzbdav/canary-plan")]
    public Task<IActionResult> GenerateCanaryPlan() => GuardedAsync(async () =>
    {
        var report = await BuildCorrelationReportAsync().ConfigureAwait(false);
        if (report.AmbiguityCount != 0)
            throw new BadHttpRequestException(
                $"Resolve all {report.AmbiguityCount} ambiguous or duplicate correlation(s) before generating a plan.");
        var session = await RequireNzbDavSessionAsync().ConfigureAwait(false);
        if (session.CurrentRunId is null)
            throw new BadHttpRequestException("The completed import has no migration run identity.");
        var planDirectory = NzbDavCanaryPlanWriter.GetPlanDirectory(
            Path.Join(DavDatabaseContext.ConfigPath, "migration-output", "nzbdav"),
            session.CurrentRunId.Value,
            report.PackageDigest);
        if (Directory.Exists(planDirectory))
            return Conflict(new BaseApiResponse { Status = false, Error = "The immutable canary plan already exists." });
        await using var context = store.NewContext();
        var result = await new NzbDavCanaryLinkPlanner().GenerateAsync(
            context, session.SourcePackageRoot!, session.CurrentRunId.Value, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new
        {
            status = true,
            result.Plan.RunId,
            result.Plan.SourcePackageDigest,
            result.Plan.SelectedCount,
            result.Plan.ActionableCount,
            download = "/api/migration/nzbdav/canary-plan",
        });
    });

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
}

public sealed record NzbDavConnectRequest(string? PackagePath, int? MaxQueueDepth, int? SubmitWorkers);
public sealed record NzbDavRunRequest(string? PackageDigest, int? SelectionCount);
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
