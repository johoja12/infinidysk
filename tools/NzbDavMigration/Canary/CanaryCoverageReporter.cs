using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;

namespace NzbDavMigration.Canary;

public sealed record CanaryCoverageItem(
    string LibraryRelativePath,
    Guid LegacyDavItemId,
    string Classification);

public sealed record CanaryCoverageReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    int InitialSourceCount,
    int FinalSourceCount,
    int AddedCount,
    int RemovedCount,
    int ParallelLinkCount,
    int OrphanParallelCount,
    int CoveredCount,
    int UncoveredCount,
    decimal CoverageFraction,
    decimal MinimumCoverage,
    bool MeetsMinimumCoverage,
    IReadOnlyList<CanaryCoverageItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CanaryCoverageWriteResult(
    CanaryCoverageReport Report,
    bool HasOwnershipErrors);

public sealed class CanaryCoverageReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<CanaryCoverageWriteResult> WriteAsync(
        string sourceRoot,
        string libraryRoot,
        string initialInventoryPath,
        string masterManifestPath,
        string journalsDirectory,
        string outputDirectory,
        decimal minimumCoverage = 0.90m,
        TimeSpan? statTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (minimumCoverage is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumCoverage));
        var source = CanaryPathSafety.ResolveRoot(sourceRoot, "Source library root");
        var library = CanaryPathSafety.ResolveRoot(libraryRoot, "Parallel library root");
        var journals = CanaryPathSafety.ResolveRoot(journalsDirectory, "Journals directory");
        var initial = await ReadJsonAsync<LegacyInventoryCandidate[]>(initialInventoryPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Initial inventory is empty.");
        var masterItems = await ReadMasterItemsAsync(masterManifestPath, cancellationToken).ConfigureAwait(false);
        var initialByPath = UniqueByPath(initial.Select(item =>
            new LibraryInventoryLink(item.LibraryRelativePath, item.OriginalTarget, item.LegacyDavItemId)),
            "initial inventory");
        var final = new LibraryInventoryService().Inventory(source);
        var finalByPath = UniqueByPath(final, "final inventory");
        var parallel = new LibraryInventoryService().Inventory(library);
        var orphanParallelCount = parallel.Count(item => !finalByPath.ContainsKey(item.LibraryRelativePath));
        var masterByPath = UniqueMasterByPath(masterItems);
        var ownership = await ReadOwnershipAsync(
            journals, source, library, statTimeout ?? TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        var items = new List<CanaryCoverageItem>(final.Count);
        var hasOwnershipErrors = orphanParallelCount != 0;
        foreach (var current in final)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string classification;
            var existedInitially = initialByPath.TryGetValue(current.LibraryRelativePath, out var original);
            if (existedInitially && (original!.LegacyDavItemId != current.LegacyDavItemId
                                     || !string.Equals(original.OriginalTarget, current.OriginalTarget,
                                         StringComparison.Ordinal)))
                classification = "source-drift";
            else if (!masterByPath.TryGetValue(current.LibraryRelativePath, out var recovered))
                classification = existedInitially ? "not-in-master" : "added-after-initial";
            else if (recovered.LegacyDavItemId != current.LegacyDavItemId
                     || !string.Equals(recovered.OriginalTarget, current.OriginalTarget, StringComparison.Ordinal))
                classification = "master-source-mismatch";
            else if (recovered.Classification is not ("exact-direct" or "exact-archive"))
                classification = recovered.Classification;
            else if (!ownership.TryGetValue(current.LibraryRelativePath, out var owned))
            {
                var parallelPath = CanaryPathSafety.ResolveBeneath(
                    library, current.LibraryRelativePath, "parallel library path");
                classification = CanaryPathSafety.PathExistsNoFollow(parallelPath)
                    ? "unowned-parallel"
                    : "missing-parallel";
                hasOwnershipErrors |= classification == "unowned-parallel";
            }
            else
            {
                classification = owned.Classification;
                hasOwnershipErrors |= owned.IsOwnershipError;
            }
            items.Add(new CanaryCoverageItem(
                current.LibraryRelativePath, current.LegacyDavItemId, classification));
        }

        var covered = items.Count(item => item.Classification == "covered");
        var fraction = items.Count == 0 ? 0m : decimal.Divide(covered, items.Count);
        var report = new CanaryCoverageReport(
            CanaryCoverageReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            initialByPath.Count,
            finalByPath.Count,
            finalByPath.Keys.Count(path => !initialByPath.ContainsKey(path)),
            initialByPath.Keys.Count(path => !finalByPath.ContainsKey(path)),
            parallel.Count,
            orphanParallelCount,
            covered,
            items.Count - covered,
            fraction,
            minimumCoverage,
            fraction >= minimumCoverage,
            items.OrderBy(item => item.LibraryRelativePath, StringComparer.Ordinal).ToArray());
        await WriteBundleAsync(outputDirectory, report, cancellationToken).ConfigureAwait(false);
        return new CanaryCoverageWriteResult(report, hasOwnershipErrors);
    }

    private static async Task<Dictionary<string, OwnershipEvaluation>> ReadOwnershipAsync(
        string journalsDirectory,
        string sourceRoot,
        string libraryRoot,
        TimeSpan statTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(statTimeout, TimeSpan.Zero);
        var result = new Dictionary<string, OwnershipEvaluation>(StringComparer.Ordinal);
        foreach (var journalPath in EnumerateJournalFilesNoFollow(new DirectoryInfo(journalsDirectory))
                     .Order(StringComparer.Ordinal))
        {
            var journal = await CanaryJournalStore.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidDataException($"Apply journal is empty: {journalPath}");
            if (journal.SchemaVersion != 2
                || !string.Equals(journal.SourceRoot, sourceRoot, StringComparison.Ordinal)
                || !string.Equals(journal.LibraryRoot, libraryRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"Apply journal has an unsupported schema or root set: {journalPath}");
            var targetRoot = CanaryPathSafety.ResolveRoot(journal.TargetRoot, "Journal target root");
            var verified = await CanaryPlanVerifier.ReadAsync(journal.PlanPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(verified.PlanSha256, journal.PlanSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Apply journal plan digest mismatch: {journalPath}");
            var planned = verified.Plan.Links.ToDictionary(item => item.LibraryRelativePath, StringComparer.Ordinal);
            foreach (var link in journal.Links.Where(item => item.Status == "applied"))
            {
                if (!result.TryAdd(link.LibraryRelativePath, default!))
                    throw new InvalidDataException($"Multiple journals claim '{link.LibraryRelativePath}'.");
                if (!planned.TryGetValue(link.LibraryRelativePath, out var plan)
                    || plan.CorrelationStatus != "exact"
                    || plan.ApplyStatus != "planned"
                    || plan.NewRelativeTarget is null)
                    throw new InvalidDataException($"Journal claims a link absent from its exact plan: {journalPath}");
                var expectedSource = CanaryPathSafety.ResolveBeneath(
                    sourceRoot, plan.LibraryRelativePath, "source library path");
                var expectedLink = CanaryPathSafety.ResolveBeneath(
                    libraryRoot, plan.LibraryRelativePath, "parallel library path");
                var expectedTarget = CanaryPathSafety.ResolveBeneath(
                    targetRoot, plan.NewRelativeTarget, "target path");
                if (!string.Equals(link.SourceLinkPath, expectedSource, StringComparison.Ordinal)
                    || !string.Equals(link.ObservedSourceTarget, plan.OriginalLegacyTarget, StringComparison.Ordinal)
                    || !string.Equals(link.LinkPath, expectedLink, StringComparison.Ordinal)
                    || !string.Equals(link.TargetPath, expectedTarget, StringComparison.Ordinal)
                    || link.ExpectedFileSize != plan.ExpectedFileSize)
                    throw new InvalidDataException($"Journal ownership fields disagree with its plan: {journalPath}");
                result[link.LibraryRelativePath] = await EvaluateOwnershipAsync(
                    link, statTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        return result;
    }

    private static async Task<OwnershipEvaluation> EvaluateOwnershipAsync(
        CanaryApplyJournalLink link,
        TimeSpan statTimeout,
        CancellationToken cancellationToken)
    {
        if (!CanaryPathSafety.PathExistsNoFollow(link.LinkPath))
            return new OwnershipEvaluation("missing-parallel", false);
        var info = new FileInfo(link.LinkPath);
        info.Refresh();
        if (!string.Equals(info.LinkTarget, link.TargetPath, StringComparison.Ordinal))
            return new OwnershipEvaluation("wrong-target", true);
        try
        {
            var valid = await Task.Run(() =>
            {
                var target = new FileInfo(link.TargetPath);
                target.Refresh();
                return target.Exists && target.LinkTarget is null && target.Length == link.ExpectedFileSize;
            }, CancellationToken.None).WaitAsync(statTimeout, cancellationToken).ConfigureAwait(false);
            return valid
                ? new OwnershipEvaluation("covered", false)
                : new OwnershipEvaluation("target-invalid", true);
        }
        catch (TimeoutException)
        {
            return new OwnershipEvaluation("target-timeout", true);
        }
    }

    private static void ValidateMaster(FullRecoveryMasterManifest master)
    {
        var recoverable = master.Items.Count(item => item.Classification is "exact-direct" or "exact-archive");
        var fraction = master.Items.Count == 0 ? 0m : decimal.Divide(recoverable, master.Items.Count);
        if (master.SchemaVersion != FullRecoveryMasterManifest.CurrentSchemaVersion
            || master.TotalLinks != master.Items.Count
            || master.RecoverableLinks != recoverable
            || master.RecoverableFraction != fraction)
            throw new InvalidDataException("Master manifest counts or schema are invalid.");
    }

    private static async Task<IReadOnlyList<LegacySourceRecoveryItem>> ReadMasterItemsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        if (directory.Exists)
        {
            directory.Refresh();
            if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Coverage master directory must not be a symbolic link.");
            var files = directory.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("Coverage master directory contains no JSON manifests.");
            var items = new List<LegacySourceRecoveryItem>();
            foreach (var file in files)
            {
                file.Refresh();
                if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Coverage master manifest must not be a symbolic link: {file.FullName}");
                var master = await ReadJsonAsync<FullRecoveryMasterManifest>(file.FullName, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException($"Master manifest is empty: {file.FullName}");
                ValidateMaster(master);
                items.AddRange(master.Items);
            }
            return items;
        }

        var single = await ReadJsonAsync<FullRecoveryMasterManifest>(fullPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Master manifest is empty.");
        ValidateMaster(single);
        return single.Items;
    }

    private static IEnumerable<string> EnumerateJournalFilesNoFollow(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            entry.Refresh();
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            if (entry is DirectoryInfo child)
            {
                foreach (var path in EnumerateJournalFilesNoFollow(child)) yield return path;
            }
            else if (entry is FileInfo file
                     && string.Equals(file.Name, "apply-journal.json", StringComparison.Ordinal))
            {
                yield return file.FullName;
            }
        }
    }

    private static Dictionary<string, LibraryInventoryLink> UniqueByPath(
        IEnumerable<LibraryInventoryLink> links,
        string description)
    {
        var result = new Dictionary<string, LibraryInventoryLink>(StringComparer.Ordinal);
        foreach (var link in links)
            if (!result.TryAdd(link.LibraryRelativePath, link))
                throw new InvalidDataException($"The {description} contains duplicate path '{link.LibraryRelativePath}'.");
        return result;
    }

    private static Dictionary<string, LegacySourceRecoveryItem> UniqueMasterByPath(
        IEnumerable<LegacySourceRecoveryItem> items)
    {
        var result = new Dictionary<string, LegacySourceRecoveryItem>(StringComparer.Ordinal);
        foreach (var item in items)
            if (!result.TryAdd(item.LibraryRelativePath, item))
                throw new InvalidDataException($"Master manifest contains duplicate path '{item.LibraryRelativePath}'.");
        return result;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.LinkTarget is not null)
            throw new FileNotFoundException("Coverage input must be a regular file.", info.FullName);
        await using var stream = info.OpenRead();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteBundleAsync(
        string outputDirectory,
        CanaryCoverageReport report,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException($"Coverage output destination already exists: {output}");
        var parent = Path.GetDirectoryName(output)
                     ?? throw new InvalidDataException("Coverage output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var stage = Path.Join(parent, $".{Path.GetFileName(output)}.tmp-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            var markdown = Encoding.UTF8.GetBytes(BuildMarkdown(report));
            await WritePrivateAsync(Path.Join(stage, "coverage.json"), json, cancellationToken).ConfigureAwait(false);
            await WritePrivateAsync(Path.Join(stage, "coverage.md"), markdown, cancellationToken).ConfigureAwait(false);
            var sums = new StringBuilder();
            foreach (var name in new[] { "coverage.json", "coverage.md" })
            {
                var bytes = await File.ReadAllBytesAsync(Path.Join(stage, name), cancellationToken)
                    .ConfigureAwait(false);
                sums.Append(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
                    .Append("  ").Append(name).Append('\n');
            }
            await WritePrivateAsync(
                Path.Join(stage, "SHA256SUMS"), Encoding.UTF8.GetBytes(sums.ToString()), cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(stage, output);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static string BuildMarkdown(CanaryCoverageReport report)
    {
        var text = new StringBuilder()
            .AppendLine("# NzbDav full-library coverage")
            .AppendLine()
            .AppendLine($"- Final source links: {report.FinalSourceCount}")
            .AppendLine($"- Covered links: {report.CoveredCount}")
            .AppendLine($"- Coverage: {report.CoverageFraction:P2}")
            .AppendLine($"- Minimum: {report.MinimumCoverage:P2}")
            .AppendLine($"- Added since initial snapshot: {report.AddedCount}")
            .AppendLine($"- Removed since initial snapshot: {report.RemovedCount}")
            .AppendLine($"- Parallel links: {report.ParallelLinkCount}")
            .AppendLine($"- Orphan parallel links: {report.OrphanParallelCount}")
            .AppendLine()
            .AppendLine("## Classifications")
            .AppendLine();
        foreach (var group in report.Items.GroupBy(item => item.Classification).OrderBy(group => group.Key))
            text.AppendLine($"- {group.Key}: {group.Count()}");
        return text.ToString();
    }

    private static async Task WritePrivateAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = CanaryPackageWriter.CreatePrivateFile(path);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private sealed record OwnershipEvaluation(string Classification, bool IsOwnershipError);
}
