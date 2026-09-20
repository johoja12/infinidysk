using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbWebDAV.UsenetMigration.NzbDav;

return await NzbDavMigrationProgram.RunAsync(args);

internal static class NzbDavMigrationProgram
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await Console.Error.WriteLineAsync("Usage: NzbDavMigration inventory --library-root PATH --output FILE");
            await Console.Error.WriteLineAsync("       NzbDavMigration export --selection FILE --inventory FILE --blob-root PATH --output DIR --package-id ID");
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "inventory" => await InventoryAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                "export" => await ExportAsync(ParseOptions(args[1..])).ConfigureAwait(false),
                _ => throw new InvalidDataException($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
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
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
        var rejected = selected.Where(candidate => !candidate.Status.StartsWith("candidate", StringComparison.Ordinal))
            .ToArray();
        if (rejected.Length > 0)
            throw new InvalidDataException($"Selection contains {rejected.Length} excluded inventory candidates.");

        var resolver = new LegacyBlobResolver(blobRoot);
        var extractor = new LegacyIdentityExtractor();
        var releases = new List<CanaryExportRelease>();
        foreach (var group in selected.GroupBy(candidate => candidate.Item!.NzbBlobId!.Value).OrderBy(group => group.Key))
        {
            var nzb = await resolver.ReadNzbAsync(group.Key).ConfigureAwait(false);
            var sourceReleaseId = group.Key.ToString();
            var leaves = group.Select(candidate => extractor.Extract(candidate.Item!, nzb.Document, sourceReleaseId))
                .ToArray();
            if (leaves.Any(leaf => !string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal)))
                throw new InvalidDataException($"Release {sourceReleaseId} contains selected leaves without strong identity.");
            releases.Add(new CanaryExportRelease(sourceReleaseId, group.Key, nzb.Path, leaves));
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
        return await JsonSerializer.DeserializeAsync<T>(stream).ConfigureAwait(false);
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
}
