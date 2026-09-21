using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using NzbDavMigration.Canary;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbDavMigration.Recovery;
using NzbWebDAV.UsenetMigration.NzbDav;

return await NzbDavMigrationProgram.RunAsync(args);

internal static class NzbDavMigrationProgram
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await Console.Error.WriteLineAsync("Usage: NzbDavMigration inventory --library-root PATH --blob-root PATH --output FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration catalogue-list --blob-root PATH --output FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration catalogue-scan --blob-root PATH --inventory FILE --database FILE --summary FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration recover-full --inventory FILE --catalogue FILE --output DIR [--minimum-coverage 0.90]");
            await Console.Error.WriteLineAsync("       NzbDavMigration export-batches --master FILE --blob-root PATH --output DIR [--max-releases 250] [--max-payload-bytes 4294967296]");
            await Console.Error.WriteLineAsync("       NzbDavMigration export --selection FILE --inventory FILE --blob-root PATH --output DIR --package-id ID");
            await Console.Error.WriteLineAsync("       NzbDavMigration apply-links --plan FILE --source-root PATH --library-root PATH --target-root PATH [--journal FILE]");
            await Console.Error.WriteLineAsync("       NzbDavMigration rollback-links --journal FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration validate-links --journal FILE --output FILE [--ffprobe PATH] [--max-read-bytes N] [--timeout-seconds N]");
            await Console.Error.WriteLineAsync("       NzbDavMigration benchmark-links --selection FILE --plan FILE --output DIR --legacy-url URL --legacy-route KIND --infinidysk-url URL --infinidysk-route KIND [--legacy-root /mnt/plex] [--infinidysk-root /mnt/plex2] [--legacy-cache-root PATH] [--infinidysk-cache-root PATH] [--timeout-seconds N]");
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "inventory" => await InventoryAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "catalogue-list" => await CatalogueListAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "catalogue-scan" => await CatalogueScanAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "recover-full" => await RecoverFullAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "export-batches" => await ExportBatchesAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "export" => await ExportAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "apply-links" => await ApplyLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "rollback-links" => await RollbackLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "validate-links" => await ValidateLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "benchmark-links" => await BenchmarkLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                _ => throw new InvalidDataException($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> CatalogueListAsync(IReadOnlyDictionary<string, string> options)
    {
        await OrphanCatalogueInputList.CreateAsync(
            Required(options, "--blob-root"), Required(options, "--output")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CatalogueScanAsync(IReadOnlyDictionary<string, string> options)
    {
        await using var store = new OrphanCatalogueStore(
            Required(options, "--database"), Required(options, "--summary"));
        await new OrphanCatalogueScanner().ScanAsync(
            Required(options, "--blob-root"), Required(options, "--inventory"), store).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RecoverFullAsync(IReadOnlyDictionary<string, string> options)
    {
        var inventory = await ReadJsonAsync<LegacyInventoryCandidate[]>(Required(options, "--inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Inventory file is empty.");
        var cataloguePath = Required(options, "--catalogue");
        await using var store = new OrphanCatalogueStore(
            cataloguePath, cataloguePath + ".completion.json");
        await store.OpenCompletedAsync().ConfigureAwait(false);
        var minimum = ParseCoverage(options, "--minimum-coverage", 0.90m);
        var result = await new LegacySourceRecovery().WriteAsync(
            inventory, store, Required(options, "--output"), minimum).ConfigureAwait(false);
        return result.MeetsMinimumCoverage ? 0 : 3;
    }

    private static async Task<int> ExportBatchesAsync(IReadOnlyDictionary<string, string> options)
    {
        var masterPath = Required(options, "--master");
        var masterBytes = await File.ReadAllBytesAsync(masterPath).ConfigureAwait(false);
        var master = JsonSerializer.Deserialize<FullRecoveryMasterManifest>(masterBytes, ReportJsonOptions)
            ?? throw new InvalidDataException("Master recovery manifest is empty.");
        if (master.RecoverableLinks == 0 || master.RecoverableFraction < 0.90m)
            throw new InvalidDataException("Master recovery manifest has not passed the 90% coverage gate.");
        var masterDigest = Convert.ToHexString(SHA256.HashData(masterBytes)).ToLowerInvariant();
        var blobRoot = Required(options, "--blob-root");
        var releases = new List<FullRecoveryRelease>();
        foreach (var group in master.Items.Where(item =>
                     item.Classification is "exact-direct" or "exact-archive")
                 .GroupBy(item => item.PayloadSha256, StringComparer.Ordinal))
        {
            if (group.Key is null || group.Any(item => item.SourceRelativePath is null))
                throw new InvalidDataException("Recoverable master rows require payload provenance.");
            var sourceRelativePath = group.Select(item => item.SourceRelativePath!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).First();
            var sourcePath = OrphanCatalogueInputList.ResolveSafePath(blobRoot, sourceRelativePath);
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("A recovered payload is missing or unsafe.");
            await using var payload = File.OpenRead(sourcePath);
            var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(payload).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actualDigest, group.Key, StringComparison.Ordinal))
                throw new InvalidDataException("A recovered payload changed after catalogue sealing.");
            releases.Add(new FullRecoveryRelease(group.Key, group.Key, sourceRelativePath, info.Length,
                group.OrderBy(item => item.LibraryRelativePath, StringComparer.Ordinal).ToArray()));
        }
        var batches = new BatchPackagePlanner().Partition(releases,
            ParsePositiveInt(options, "--max-releases", 250),
            ParsePositiveLong(options, "--max-payload-bytes", 4L * 1024 * 1024 * 1024));
        var output = Path.GetFullPath(Required(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException($"Batch export destination already exists: {output}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            foreach (var batch in batches)
            {
                var exportReleases = batch.Releases.Select(release => new CanaryExportRelease(
                    release.SourceReleaseId,
                    null,
                    OrphanCatalogueInputList.ResolveSafePath(blobRoot, release.PayloadRelativePath),
                    release.Items.Select(item => new NzbDavExportLeaf(
                        item.LegacyDavItemId,
                        item.LegacyPath ?? throw new InvalidDataException("Recovered leaf is missing its legacy path."),
                        item.FileSize ?? throw new InvalidDataException("Recovered leaf is missing its exact size."),
                        release.SourceReleaseId,
                        null,
                        null,
                        item.IdentityKind ?? throw new InvalidDataException("Recovered leaf is missing identity kind."),
                        item.IdentityDigest,
                        "ready",
                        null,
                        item.Classification == "exact-archive" ? item.IdentityKind : null,
                        item.Classification == "exact-archive" ? item.IdentityDigest : null)).ToArray())).ToArray();
                var selected = batch.Releases.SelectMany(release => release.Items).Select(item =>
                    new NzbDavSelectedLibraryLink(item.LibraryRelativePath, item.OriginalTarget,
                        item.LegacyDavItemId)).ToArray();
                var request = new CanaryExportRequest(
                    $"full-{masterDigest[..12]}-{batch.BatchIndex + 1:D4}",
                    Path.Join(output, $"batch-{batch.BatchIndex + 1:D4}"), exportReleases, selected);
                await new CanaryPackageWriter().WriteFullBatchAsync(
                    request, masterDigest, batch.BatchIndex, batches.Count).ConfigureAwait(false);
            }
            return 0;
        }
        catch
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            throw;
        }
    }

    private static async Task<int> ApplyLinksAsync(IReadOnlyDictionary<string, string> options)
    {
        var plan = Required(options, "--plan");
        var journal = Optional(options, "--journal")
                      ?? Path.Join(Path.GetDirectoryName(Path.GetFullPath(plan))!, "apply-journal.json");
        await new CanaryLinkApplier().ApplyAsync(
            plan,
            Required(options, "--source-root"),
            Required(options, "--library-root"),
            Required(options, "--target-root"),
            journal).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(journal);
        return 0;
    }

    private static async Task<int> RollbackLinksAsync(IReadOnlyDictionary<string, string> options)
    {
        var result = await new CanaryLinkRollback().RollbackAsync(Required(options, "--journal"))
            .ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ReportJsonOptions));
        return 0;
    }

    private static async Task<int> ValidateLinksAsync(IReadOnlyDictionary<string, string> options)
    {
        var output = Required(options, "--output");
        var maxRead = ParsePositiveInt(options, "--max-read-bytes", 64 * 1024);
        var timeout = TimeSpan.FromSeconds(ParsePositiveInt(options, "--timeout-seconds", 10));
        var results = await new CanaryValidator().ValidateAsync(
            Required(options, "--journal"), maxRead, timeout, Optional(options, "--ffprobe"))
            .ConfigureAwait(false);
        await WriteJsonCreateNewAsync(output, results).ConfigureAwait(false);
        return results.All(result => result.Success) ? 0 : 1;
    }

    private static async Task<int> BenchmarkLinksAsync(IReadOnlyDictionary<string, string> options)
    {
        var selection = await CanaryBenchmarkSelectionReader.ReadAndValidateAsync(
            Required(options, "--selection"), Required(options, "--plan")).ConfigureAwait(false);
        var metadata = new CanaryBenchmarkMetadata(
            new CanaryRouteDescription(
                Required(options, "--legacy-url"), Required(options, "--legacy-route")),
            new CanaryRouteDescription(
                Required(options, "--infinidysk-url"), Required(options, "--infinidysk-route")));
        var results = await new CanaryBenchmarkRunner().RunAsync(
            selection,
            metadata,
            Optional(options, "--legacy-root") ?? "/mnt/plex",
            Optional(options, "--infinidysk-root") ?? "/mnt/plex2",
            TimeSpan.FromSeconds(ParsePositiveInt(options, "--timeout-seconds", 30)),
            Optional(options, "--legacy-cache-root"),
            Optional(options, "--infinidysk-cache-root"))
            .ConfigureAwait(false);
        await new CanaryPerformanceReportWriter().WriteAsync(Required(options, "--output"), results)
            .ConfigureAwait(false);
        return results.Observations.Any(result => result.Error is not null) ? 1 : 0;
    }

    private static async Task<int> InventoryAsync(IReadOnlyDictionary<string, string> options)
    {
        var root = Required(options, "--library-root");
        var output = Required(options, "--output");
        var blobRoot = Required(options, "--blob-root");
        var links = new LibraryInventoryService().Inventory(root);
        var rows = await new LegacyNzbDavReader().ReadAsync(links.Select(link => link.LegacyDavItemId))
            .ConfigureAwait(false);
        var resolver = new LegacyBlobResolver(blobRoot);
        var report = new LibraryInventoryService().Enrich(links, rows.Items, resolver);
        await using var stream = CanaryPackageWriter.CreatePrivateFile(output);
        await JsonSerializer.SerializeAsync(stream, report, ReportJsonOptions)
            .ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
#pragma warning disable CA1849 // A durable inventory requires an fsync after the async flush.
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        return 0;
    }

    private static async Task<int> ExportAsync(IReadOnlyDictionary<string, string> options)
    {
        var inventoryPath = Required(options, "--inventory");
        var selectionPath = Required(options, "--selection");
        var blobRoot = Required(options, "--blob-root");
        var output = Required(options, "--output");
        var packageId = Required(options, "--package-id");

        var inventory = await ReadJsonAsync<LegacyInventoryCandidate[]>(inventoryPath).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Inventory file is empty.");
        var inventoryLinks = inventory.Select(item => new LibraryInventoryLink(
            item.LibraryRelativePath, item.OriginalTarget, item.LegacyDavItemId)).ToArray();
        var selection = await SelectionFile.LoadAndValidateAsync(
            selectionPath, inventoryLinks, minimum: 20, maximum: 50).ConfigureAwait(false);
        var selected = selection.Items.Select(item => inventory.Single(candidate =>
                candidate.LibraryRelativePath == item.LibraryRelativePath
                && candidate.LegacyDavItemId == item.LegacyDavItemId))
            .ToArray();
        var rejected = selected.Where(candidate => candidate.Status != "candidate"
            || candidate.Item?.HistoryItemId is null || candidate.Item.NzbBlobId is null
            || candidate.Item.ResolutionExclusion is not null)
            .ToArray();
        if (rejected.Length > 0)
            throw new InvalidDataException($"Selection contains {rejected.Length} excluded inventory candidates.");

        var resolver = new LegacyBlobResolver(blobRoot);
        var extractor = new LegacyIdentityExtractor();
        var releases = new List<CanaryExportRelease>();
        foreach (var group in selected.GroupBy(candidate => candidate.Item!.NzbBlobId!.Value).OrderBy(group => group.Key))
        {
            var retained = group.Select(candidate => candidate.Item!.NzbContents)
                .Where(contents => !string.IsNullOrWhiteSpace(contents)).Distinct(StringComparer.Ordinal).ToArray();
            if (retained.Length > 1)
                throw new InvalidDataException("Selected release has inconsistent retained history NZBs; refresh inventory.");
            var nzb = await resolver.ReadNzbAsync(group.Key, retained.SingleOrDefault()).ConfigureAwait(false);
            var sourceReleaseId = group.Key.ToString();
            var leaves = group.Select(candidate => extractor.Extract(candidate.Item!, nzb.Document, sourceReleaseId))
                .ToArray();
            if (leaves.Any(leaf => !string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal)))
                throw new InvalidDataException($"Release {sourceReleaseId} contains selected leaves without strong identity.");
            releases.Add(new CanaryExportRelease(sourceReleaseId, group.Key, nzb.Path, leaves, nzb.Bytes));
        }

        var selectedLinks = selected.Select(candidate => new NzbDavSelectedLibraryLink(
            candidate.LibraryRelativePath, candidate.OriginalTarget, candidate.LegacyDavItemId)).ToArray();
        await new CanaryPackageWriter().WriteAsync(
            new CanaryExportRequest(packageId, output, releases, selectedLinks)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, ReportJsonOptions).ConfigureAwait(false);
    }

    private static async Task WriteJsonCreateNewAsync<T>(string path, T value)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, ReportJsonOptions).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
#pragma warning disable CA1849
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
            throw new InvalidDataException("Options must be supplied as --name value pairs.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[index], args[index + 1]))
                throw new InvalidDataException($"Invalid or duplicate option '{args[index]}'.");
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"{name} is required.");

    private static string? Optional(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ParsePositiveInt(
        IReadOnlyDictionary<string, string> options,
        string name,
        int defaultValue)
    {
        var value = Optional(options, name);
        if (value is null)
            return defaultValue;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"{name} must be a positive integer.");
        return parsed;
    }

    private static decimal ParseCoverage(
        IReadOnlyDictionary<string, string> options,
        string name,
        decimal defaultValue)
    {
        var value = Optional(options, name);
        if (value is null) return defaultValue;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 0 or > 1)
            throw new InvalidDataException($"{name} must be between 0 and 1.");
        return parsed;
    }

    private static long ParsePositiveLong(
        IReadOnlyDictionary<string, string> options,
        string name,
        long defaultValue)
    {
        var value = Optional(options, name);
        if (value is null) return defaultValue;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"{name} must be a positive integer.");
        return parsed;
    }
}
