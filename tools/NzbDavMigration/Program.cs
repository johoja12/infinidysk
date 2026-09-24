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
            await Console.Error.WriteLineAsync("       NzbDavMigration mapped-inventory --library-root PATH --legacy-ids-root PATH --blob-root PATH --output FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration catalogue-list --blob-root PATH --output FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration catalogue-scan --blob-root PATH --inventory FILE --database FILE --summary FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration recover-full --inventory FILE --catalogue FILE --output DIR [--minimum-coverage 0.90]");
            await Console.Error.WriteLineAsync("       NzbDavMigration verify-mapped-roots --plex-inventory FILE --special-inventory FILE --plex-master FILE --special-master FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration export-batches --master FILE --peer-master FILE --peer-inventory FILE --blob-root PATH --output DIR [--max-releases 250] [--max-payload-bytes 4294967296]");
            await Console.Error.WriteLineAsync("       NzbDavMigration export --selection FILE --inventory FILE --blob-root PATH --output DIR --package-id ID");
            await Console.Error.WriteLineAsync("       NzbDavMigration apply-links --plan FILE --source-root PATH --library-root PATH --target-root PATH [--journal FILE]");
            await Console.Error.WriteLineAsync("       NzbDavMigration apply-mapped-links --plan FILE --mapped-inventory FILE --blob-root PATH --source-root PATH --library-root PATH --target-root PATH [--journal FILE]");
            await Console.Error.WriteLineAsync("       NzbDavMigration coverage-report --source-root PATH --library-root PATH --initial-inventory FILE --master FILE_OR_DIR --journals-dir DIR --output DIR [--minimum-coverage 0.90]");
            await Console.Error.WriteLineAsync("       NzbDavMigration mapped-coverage-report --source-root PATH --library-root PATH --initial-inventory FILE --blob-root PATH --master FILE_OR_DIR --journals-dir DIR --output DIR [--minimum-coverage 0.90]");
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
                "mapped-inventory" => await MappedInventoryAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "catalogue-list" => await CatalogueListAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "catalogue-scan" => await CatalogueScanAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "recover-full" => await RecoverFullAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "verify-mapped-roots" => await VerifyMappedRootsAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "export-batches" => await ExportBatchesAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "export" => await ExportAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "apply-links" => await ApplyLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "apply-mapped-links" => await ApplyMappedLinksAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "coverage-report" => await CoverageReportAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "mapped-coverage-report" => await MappedCoverageReportAsync(ParseOptions(args[1..])).ConfigureAwait(false),
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
        var inventory = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Mapped inventory file is empty.");
        inventory.Validate();
        var cataloguePath = Required(options, "--catalogue");
        await using var store = new OrphanCatalogueStore(
            cataloguePath, cataloguePath + ".completion.json");
        await store.OpenCompletedAsync().ConfigureAwait(false);
        var minimum = ParseCoverage(options, "--minimum-coverage", 0.90m);
        var result = await new LegacySourceRecovery().WriteAsync(
            inventory.Rows.Select(row => row.Candidate).ToArray(), store, Required(options, "--output"), minimum,
            mappedSource: new MappedSourceProof(inventory.SourceRoot, inventory.LegacyIdsRoot,
                inventory.Rows.Count, inventory.RowsSha256)).ConfigureAwait(false);
        return result.MeetsMinimumCoverage ? 0 : 3;
    }

    private static async Task<int> VerifyMappedRootsAsync(IReadOnlyDictionary<string, string> options)
    {
        var plex = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--plex-inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Plex mapped inventory is empty.");
        var special = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--special-inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Special mapped inventory is empty.");
        plex.Validate();
        special.Validate();
        if (plex.SourceRoot != "/mnt/plex" || special.SourceRoot != "/mnt/special")
            throw new InvalidDataException("Cross-root verification requires the two configured legacy roots.");
        var plexMaster = await ReadJsonAsync<FullRecoveryMasterManifest>(Required(options, "--plex-master"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Plex recovery master is empty.");
        var specialMaster = await ReadJsonAsync<FullRecoveryMasterManifest>(Required(options, "--special-master"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Special recovery master is empty.");
        ValidateMappedMaster(plexMaster);
        ValidateMappedMaster(specialMaster);
        ValidateMasterMatchesInventory(plexMaster, plex);
        ValidateMasterMatchesInventory(specialMaster, special);
        if (plexMaster.RecoverableFraction < 0.90m || specialMaster.RecoverableFraction < 0.90m)
            throw new InvalidDataException("Both roots must pass 90% mapped recovery before export.");
        var sharedIds = plex.Rows.Select(row => row.DavItemId).ToHashSet()
            .Intersect(special.Rows.Select(row => row.DavItemId)).Count();
        var sharedPayloads = plexMaster.Items.Where(item => item.Classification is "exact-direct" or "exact-archive")
            .Select(item => item.PayloadSha256).Where(value => value is not null).ToHashSet(StringComparer.Ordinal)
            .Intersect(specialMaster.Items.Where(item => item.Classification is "exact-direct" or "exact-archive")
                .Select(item => item.PayloadSha256).Where(value => value is not null), StringComparer.Ordinal)
            .Count();
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            plexMappedRows = plex.Rows.Count,
            specialMappedRows = special.Rows.Count,
            sharedDavItemIds = sharedIds,
            sharedNzbPayloads = sharedPayloads,
            mayExport = sharedIds == 0 && sharedPayloads == 0,
        }, ReportJsonOptions));
        return sharedIds == 0 && sharedPayloads == 0 ? 0 : 3;
    }

    private static async Task<int> ExportBatchesAsync(IReadOnlyDictionary<string, string> options)
    {
        var masterPath = Required(options, "--master");
        var masterBytes = await File.ReadAllBytesAsync(masterPath).ConfigureAwait(false);
        var master = JsonSerializer.Deserialize<FullRecoveryMasterManifest>(masterBytes, ReportJsonOptions)
            ?? throw new InvalidDataException("Master recovery manifest is empty.");
        ValidateMappedMaster(master);
        var mappedSource = master.MappedSource
            ?? throw new InvalidDataException("Batch export requires a mapped LocalLinks source proof.");
        if (master.RecoverableLinks == 0 || master.RecoverableFraction < 0.90m)
            throw new InvalidDataException("Master recovery manifest has not passed the 90% coverage gate.");
        var masterDigest = Convert.ToHexString(SHA256.HashData(masterBytes)).ToLowerInvariant();
        var blobRoot = Required(options, "--blob-root");
        var currentMapping = await BuildMappedInventoryAsync(
            mappedSource.SourceRoot, mappedSource.LegacyIdsRoot, blobRoot).ConfigureAwait(false);
        if (currentMapping.Rows.Count != mappedSource.RowCount
            || !string.Equals(currentMapping.RowsSha256, mappedSource.RowsSha256, StringComparison.Ordinal))
            throw new InvalidDataException("LocalLinks or source symlinks changed since mapped inventory; re-inventory.");
        ValidateMasterMatchesInventory(master, currentMapping);
        var peerInventory = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--peer-inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Peer mapped inventory is empty.");
        peerInventory.Validate();
        var currentPeer = await BuildMappedInventoryAsync(
            peerInventory.SourceRoot, peerInventory.LegacyIdsRoot, blobRoot).ConfigureAwait(false);
        if (currentPeer.RowsSha256 != peerInventory.RowsSha256)
            throw new InvalidDataException("Peer LocalLinks or source symlinks changed since mapped inventory.");
        var peerMaster = await ReadJsonAsync<FullRecoveryMasterManifest>(Required(options, "--peer-master"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Peer recovery master is empty.");
        ValidateMappedMaster(peerMaster);
        ValidateMasterMatchesInventory(peerMaster, peerInventory);
        if (!((currentMapping.SourceRoot == "/mnt/plex" && peerInventory.SourceRoot == "/mnt/special")
              || (currentMapping.SourceRoot == "/mnt/special" && peerInventory.SourceRoot == "/mnt/plex"))
            || peerMaster.RecoverableFraction < 0.90m)
            throw new InvalidDataException("Peer root has not passed mapped recovery.");
        var currentIds = currentMapping.Rows.Select(row => row.DavItemId).ToHashSet();
        var peerIds = peerInventory.Rows.Select(row => row.DavItemId).ToHashSet();
        var currentPayloads = master.Items.Where(item =>
                item.Classification is "exact-direct" or "exact-archive")
            .Select(item => item.PayloadSha256).Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        var peerPayloads = peerMaster.Items.Where(item =>
                item.Classification is "exact-direct" or "exact-archive")
            .Select(item => item.PayloadSha256).Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (currentIds.Overlaps(peerIds) || currentPayloads.Overlaps(peerPayloads))
            throw new InvalidDataException(
                "The roots share mapped leaves or NZB payloads; target reuse must be implemented before export.");
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
                var exportReleases = batch.Releases.Select(release =>
                {
                    var sourceNames = NzbDavSourceNameResolver.FromLegacyPaths(release.Items.Select(item =>
                        item.LegacyPath ?? throw new InvalidDataException(
                            "Recovered leaf is missing its legacy path.")));
                    var leaves = release.Items.Select(item => new NzbDavExportLeaf(
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
                        item.Classification == "exact-archive" ? item.IdentityDigest : null)).ToArray();
                    return new CanaryExportRelease(
                        release.SourceReleaseId,
                        null,
                        OrphanCatalogueInputList.ResolveSafePath(blobRoot, release.PayloadRelativePath),
                        leaves,
                        SourceFileName: sourceNames.FileName,
                        SourceJobName: sourceNames.JobName);
                }).ToArray();
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

    private static async Task<int> ApplyMappedLinksAsync(IReadOnlyDictionary<string, string> options)
    {
        var snapshot = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--mapped-inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Mapped inventory is empty.");
        snapshot.Validate();
        var sourceRoot = Path.GetFullPath(Required(options, "--source-root"))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (sourceRoot != snapshot.SourceRoot)
            throw new InvalidDataException("Apply source root disagrees with the mapped inventory.");
        var current = await BuildMappedInventoryAsync(
            sourceRoot, snapshot.LegacyIdsRoot, Required(options, "--blob-root"))
            .ConfigureAwait(false);
        if (current.RowsSha256 != snapshot.RowsSha256)
            throw new InvalidDataException("LocalLinks or source symlinks changed since mapped inventory; re-inventory.");
        var planPath = Required(options, "--plan");
        var verified = await CanaryPlanVerifier.ReadAsync(planPath).ConfigureAwait(false);
        var byPath = current.Rows.ToDictionary(row => row.Candidate.LibraryRelativePath, StringComparer.Ordinal);
        foreach (var planned in verified.Plan.Links)
        {
            if (!byPath.TryGetValue(planned.LibraryRelativePath, out var row)
                || row.IsBroken || row.Candidate.ExclusionReason is not null
                || row.DavItemId != planned.LegacyDavItemId
                || !string.Equals(row.Candidate.OriginalTarget, planned.OriginalLegacyTarget,
                    StringComparison.Ordinal))
                throw new InvalidDataException("Plan contains a link outside the current mapped allowlist.");
        }
        var journal = Optional(options, "--journal")
                      ?? Path.Join(Path.GetDirectoryName(Path.GetFullPath(planPath))!, "apply-journal.json");
        var reader = new LegacyNzbDavReader();
        await new CanaryLinkApplier(CanaryPathSafety.IsLocalMount,
            verifyMapping: (planned, path, cancellationToken) =>
                reader.AssertMappedLinkAsync(path, planned.LegacyDavItemId, cancellationToken))
            .ApplyAsync(planPath, sourceRoot, Required(options, "--library-root"),
                Required(options, "--target-root"), journal).ConfigureAwait(false);
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

    private static async Task<int> CoverageReportAsync(IReadOnlyDictionary<string, string> options)
    {
        var result = await new CanaryCoverageReporter().WriteAsync(
            Required(options, "--source-root"),
            Required(options, "--library-root"),
            Required(options, "--initial-inventory"),
            Required(options, "--master"),
            Required(options, "--journals-dir"),
            Required(options, "--output"),
            ParseCoverage(options, "--minimum-coverage", 0.90m)).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(Path.GetFullPath(Required(options, "--output")));
        if (result.HasOwnershipErrors) return 1;
        return result.Report.MeetsMinimumCoverage ? 0 : 3;
    }

    private static async Task<int> MappedCoverageReportAsync(IReadOnlyDictionary<string, string> options)
    {
        var initial = await ReadJsonAsync<MappedLibraryInventory>(Required(options, "--initial-inventory"))
            .ConfigureAwait(false) ?? throw new InvalidDataException("Mapped initial inventory is empty.");
        initial.Validate();
        var sourceRoot = Required(options, "--source-root");
        if (Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) != initial.SourceRoot)
            throw new InvalidDataException("Coverage source root disagrees with mapped initial inventory.");
        var final = await BuildMappedInventoryAsync(
            initial.SourceRoot, initial.LegacyIdsRoot, Required(options, "--blob-root"))
            .ConfigureAwait(false);
        var result = await new CanaryCoverageReporter().WriteAsync(
            sourceRoot,
            Required(options, "--library-root"),
            Required(options, "--initial-inventory"),
            Required(options, "--master"),
            Required(options, "--journals-dir"),
            Required(options, "--output"),
            ParseCoverage(options, "--minimum-coverage", 0.90m),
            initialMapped: initial,
            finalMapped: final).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(Path.GetFullPath(Required(options, "--output")));
        if (result.HasOwnershipErrors) return 1;
        return result.Report.MeetsMinimumCoverage ? 0 : 3;
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

    private static async Task<int> MappedInventoryAsync(IReadOnlyDictionary<string, string> options)
    {
        var report = await BuildMappedInventoryAsync(
            Required(options, "--library-root"), Required(options, "--legacy-ids-root"),
            Required(options, "--blob-root")).ConfigureAwait(false);
        await WriteJsonCreateNewAsync(Required(options, "--output"), report).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            mappedRows = report.Rows.Count,
            validSourceLinks = report.Rows.Count(row => row.Candidate.ExclusionReason is null),
            excluded = report.Rows.Count(row => row.Candidate.ExclusionReason is not null),
            outOfScopeFilesystemLinks = report.OutOfScopeFilesystemLinks,
            report.RowsSha256,
        }, ReportJsonOptions));
        return 0;
    }

    private static async Task<MappedLibraryInventory> BuildMappedInventoryAsync(
        string sourceRoot,
        string legacyIdsRoot,
        string blobRoot)
    {
        var database = await new LegacyNzbDavReader().ReadMappedAsync(sourceRoot).ConfigureAwait(false);
        var filesystem = new LibraryInventoryService().Inventory(sourceRoot);
        return new MappedLibraryInventoryBuilder().Build(
            sourceRoot, legacyIdsRoot, database, filesystem, blobRoot);
    }

    private static void ValidateMappedMaster(FullRecoveryMasterManifest master)
    {
        var exact = master.Items.Count(item => item.Classification is "exact-direct" or "exact-archive");
        if (master.SchemaVersion != FullRecoveryMasterManifest.CurrentSchemaVersion
            || master.MappedSource is null
            || master.TotalLinks != master.Items.Count
            || master.RecoverableLinks != exact
            || master.RecoverableFraction != (master.TotalLinks == 0
                ? 0m : decimal.Divide(exact, master.TotalLinks))
            || master.MappedSource.RowCount != master.TotalLinks
            || master.Items.Select(item => item.LibraryRelativePath).Distinct(StringComparer.Ordinal).Count()
                != master.Items.Count)
            throw new InvalidDataException("Mapped recovery master counts or provenance are invalid.");
    }

    private static void ValidateMasterMatchesInventory(
        FullRecoveryMasterManifest master,
        MappedLibraryInventory inventory)
    {
        ValidateMappedMaster(master);
        inventory.Validate();
        if (master.MappedSource!.SourceRoot != inventory.SourceRoot
            || master.MappedSource.LegacyIdsRoot != inventory.LegacyIdsRoot
            || master.MappedSource.RowsSha256 != inventory.RowsSha256
            || master.MappedSource.RowCount != inventory.Rows.Count)
            throw new InvalidDataException("Recovery master does not match its mapped LocalLinks inventory.");
        var byPath = inventory.Rows.ToDictionary(row => row.Candidate.LibraryRelativePath, StringComparer.Ordinal);
        foreach (var item in master.Items)
        {
            if (!byPath.TryGetValue(item.LibraryRelativePath, out var mapped)
                || mapped.DavItemId != item.LegacyDavItemId
                || !string.Equals(mapped.Candidate.OriginalTarget, item.OriginalTarget, StringComparison.Ordinal)
                || (item.Classification is "exact-direct" or "exact-archive"
                    && (mapped.IsBroken || mapped.Candidate.ExclusionReason is not null)))
                throw new InvalidDataException("Recovery master contains a file outside its mapped allowlist.");
        }
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
            var historyFileNames = group.Select(candidate => candidate.Item!.HistoryFileName)
                .Distinct(StringComparer.Ordinal).ToArray();
            var historyJobNames = group.Select(candidate => candidate.Item!.HistoryJobName)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (historyFileNames.Length != 1 || historyJobNames.Length != 1)
                throw new InvalidDataException($"Release {sourceReleaseId} has inconsistent legacy history names.");
            var sourceNames = NzbDavSourceNameResolver.FromHistory(historyFileNames[0], historyJobNames[0]);
            releases.Add(new CanaryExportRelease(
                sourceReleaseId,
                group.Key,
                nzb.Path,
                leaves,
                nzb.Bytes,
                sourceNames.FileName,
                sourceNames.JobName));
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
