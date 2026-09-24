using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Legacy;

namespace NzbDavMigration.Inventory;

public sealed record MappedInventoryShard(
    int Index,
    string RelativePath,
    int RowCount,
    string FirstLinkPath,
    string LastLinkPath,
    string RowsSha256,
    string FileSha256);

public sealed record MappedAllowlistEntry(
    string LinkPath,
    Guid DavItemId,
    bool IsBroken,
    string OriginalTarget,
    string? ExclusionReason);

public sealed record ShardedMappedInventoryManifest(
    int SchemaVersion,
    string SourceRoot,
    string LegacyIdsRoot,
    DateTimeOffset CreatedAt,
    int RowCount,
    int OutOfScopeFilesystemLinks,
    string RowsSha256,
    string AllowlistSha256,
    IReadOnlyList<MappedInventoryShard> Shards)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Shards.Count == 0 || RowCount <= 0
            || OutOfScopeFilesystemLinks < 0 || !IsSha256(AllowlistSha256)
            || !Path.IsPathFullyQualified(SourceRoot) || !Path.IsPathFullyQualified(LegacyIdsRoot)
            || SourceRoot != Path.GetFullPath(SourceRoot).TrimEnd(Path.DirectorySeparatorChar)
            || LegacyIdsRoot != Path.GetFullPath(LegacyIdsRoot).TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidDataException("Invalid sharded mapped inventory header.");
        var total = 0;
        string? previous = null;
        for (var index = 0; index < Shards.Count; index++)
        {
            var shard = Shards[index];
            if (shard.Index != index || shard.RowCount <= 0
                || shard.RelativePath != $"shards/{index + 1:D6}.json"
                || !IsSha256(shard.RowsSha256) || !IsSha256(shard.FileSha256)
                || string.CompareOrdinal(shard.FirstLinkPath, shard.LastLinkPath) > 0
                || (previous is not null && string.CompareOrdinal(previous, shard.FirstLinkPath) >= 0))
                throw new InvalidDataException("Invalid sharded mapped inventory entry.");
            previous = shard.LastLinkPath;
            total = checked(total + shard.RowCount);
        }
        if (total != RowCount || RowsSha256 != ComputeRowsSha256(Shards))
            throw new InvalidDataException("Sharded mapped inventory count or digest disagrees.");
    }

    public static string ComputeRowsSha256(IEnumerable<MappedInventoryShard> shards)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var shard in shards)
            hash.AppendData(Encoding.UTF8.GetBytes($"{shard.Index}:{shard.RowCount}:{shard.RowsSha256}\n"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ShardedMappedInventoryWriter(ILegacyMappedBatchReader? batchReader = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ShardedMappedInventoryManifest> WriteAsync(
        string sourceRoot,
        string legacyIdsRoot,
        string blobRoot,
        string outputDirectory,
        int batchSize = 64,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var ids = Path.GetFullPath(legacyIdsRoot).TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(outputDirectory);
        if (File.Exists(output) || File.Exists(Path.Join(output, "manifest.json")))
            throw new IOException("A completed mapped inventory already exists at the destination.");
        if (Directory.Exists(output) && new DirectoryInfo(output).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory output cannot be a symbolic link.");
        Directory.CreateDirectory(output);
        var shardDirectory = Path.Join(output, "shards");
        Directory.CreateDirectory(shardDirectory);
        if (new DirectoryInfo(shardDirectory).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory shard directory cannot be a symbolic link.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(shardDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var filesystem = new LibraryInventoryService().Inventory(root);
        var filesystemByPath = filesystem.ToDictionary(link => link.LibraryRelativePath, StringComparer.Ordinal);
        var shards = new List<MappedInventoryShard>();
        HashSet<Guid>? duplicates = null;
        HashSet<string>? mappedPaths = null;
        int? outOfScope = null;
        var allowlistStage = Path.Join(output, $".allowlist.tmp-{Guid.NewGuid():N}");
        await using var allowlistFile = CanaryPackageWriter.CreatePrivateFile(allowlistStage);
        connectionString ??= Environment.GetEnvironmentVariable(LegacyNzbDavReader.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{LegacyNzbDavReader.ConnectionEnvironmentVariable} is required.");
        await (batchReader ?? new LegacyNzbDavReader()).ReadMappedBatchesAsync(connectionString, root, async (allMappings, batch) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (duplicates is null)
            {
                duplicates = allMappings.GroupBy(link => link.DavItemId)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
                mappedPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mapping in allMappings)
                {
                    if (!mapping.LinkPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || !mappedPaths.Add(Path.GetRelativePath(root, mapping.LinkPath)
                            .Replace(Path.DirectorySeparatorChar, '/')))
                        throw new InvalidDataException("Duplicate or escaped LocalLinks path in source database.");
                }
                outOfScope = filesystemByPath.Keys.Count(path => !mappedPaths.Contains(path));
            }
            var selected = batch.Links.Select(mapping =>
                    Path.GetRelativePath(root, mapping.LinkPath).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(filesystemByPath.ContainsKey)
                .Select(path => filesystemByPath[path]).ToArray();
            var inventory = new MappedLibraryInventoryBuilder().Build(
                root, ids, batch, selected, blobRoot, duplicates);
            foreach (var row in inventory.Rows)
            {
                var line = JsonSerializer.Serialize(new MappedAllowlistEntry(
                    row.LinkPath, row.DavItemId, row.IsBroken,
                    row.Candidate.OriginalTarget, row.Candidate.ExclusionReason), JsonOptions) + "\n";
                await allowlistFile.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken)
                    .ConfigureAwait(false);
            }
            var index = shards.Count;
            var relativePath = $"shards/{index + 1:D6}.json";
            var shardPath = Path.Join(output, relativePath);
            if (File.Exists(shardPath))
            {
                var previous = await ReadShardAsync(shardPath, cancellationToken).ConfigureAwait(false);
                if (previous.RowsSha256 != inventory.RowsSha256 || previous.Rows.Count != inventory.Rows.Count)
                    throw new InvalidDataException("An interrupted shard disagrees with the current source snapshot.");
            }
            else
            {
                var stage = shardPath + $".tmp-{Guid.NewGuid():N}";
                try
                {
                    await WritePrivateJsonAsync(stage, inventory, cancellationToken).ConfigureAwait(false);
                    File.Move(stage, shardPath);
                }
                finally
                {
                    if (File.Exists(stage)) File.Delete(stage);
                }
            }
            shards.Add(new MappedInventoryShard(index, relativePath, inventory.Rows.Count,
                inventory.Rows[0].LinkPath, inventory.Rows[^1].LinkPath, inventory.RowsSha256,
                await Sha256Async(shardPath, cancellationToken).ConfigureAwait(false)));
        }, batchSize, cancellationToken).ConfigureAwait(false);

        if (shards.Count == 0 || outOfScope is null)
            throw new InvalidDataException("No LocalLinks mappings exist beneath the selected source root.");
        await allowlistFile.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Publish only durable allowlist evidence.
        allowlistFile.Flush(flushToDisk: true);
#pragma warning restore CA1849
        await allowlistFile.DisposeAsync().ConfigureAwait(false);
        File.Move(allowlistStage, Path.Join(output, "allowlist.jsonl"), overwrite: true);
        var allowlistDigest = await Sha256Async(Path.Join(output, "allowlist.jsonl"), cancellationToken)
            .ConfigureAwait(false);
        var manifest = new ShardedMappedInventoryManifest(
            ShardedMappedInventoryManifest.CurrentSchemaVersion, root, ids, DateTimeOffset.UtcNow,
            shards.Sum(shard => shard.RowCount), outOfScope.Value,
            ShardedMappedInventoryManifest.ComputeRowsSha256(shards), allowlistDigest, shards);
        manifest.Validate();
        var manifestStage = Path.Join(output, $".manifest.tmp-{Guid.NewGuid():N}");
        try
        {
            await WritePrivateJsonAsync(manifestStage, manifest, cancellationToken).ConfigureAwait(false);
            File.Move(manifestStage, Path.Join(output, "manifest.json"));
        }
        finally
        {
            if (File.Exists(manifestStage)) File.Delete(manifestStage);
        }
        return manifest;
    }

    public static async Task<ShardedMappedInventoryManifest> ReadManifestAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        if (new DirectoryInfo(root).LinkTarget is not null
            || new FileInfo(Path.Join(root, "manifest.json")).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory artifact cannot be a symbolic link.");
        var path = Path.Join(root, "manifest.json");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<ShardedMappedInventoryManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Sharded mapped inventory manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public static async Task<MappedLibraryInventory> ReadVerifiedShardAsync(
        string directory,
        ShardedMappedInventoryManifest manifest,
        MappedInventoryShard shard,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        if (new DirectoryInfo(root).LinkTarget is not null
            || new DirectoryInfo(Path.Join(root, "shards")).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory shard directory cannot be a symbolic link.");
        var path = Path.Join(root, shard.RelativePath);
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory shard cannot be a symbolic link.");
        if (await Sha256Async(path, cancellationToken).ConfigureAwait(false) != shard.FileSha256)
            throw new InvalidDataException("Mapped inventory shard file digest changed.");
        var inventory = await ReadShardAsync(path, cancellationToken).ConfigureAwait(false);
        if (inventory.SourceRoot != manifest.SourceRoot || inventory.LegacyIdsRoot != manifest.LegacyIdsRoot
            || inventory.Rows.Count != shard.RowCount || inventory.RowsSha256 != shard.RowsSha256
            || inventory.Rows[0].LinkPath != shard.FirstLinkPath
            || inventory.Rows[^1].LinkPath != shard.LastLinkPath)
            throw new InvalidDataException("Mapped inventory shard disagrees with its sealed manifest.");
        return inventory;
    }

    public static async Task<IReadOnlyList<MappedAllowlistEntry>> ReadAllowlistAsync(
        string directory,
        ShardedMappedInventoryManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidDataException("Mapped allowlist directory cannot be a symbolic link.");
        var path = Path.Join(root, "allowlist.jsonl");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Mapped allowlist cannot be a symbolic link.");
        if (await Sha256Async(path, cancellationToken).ConfigureAwait(false) != manifest.AllowlistSha256)
            throw new InvalidDataException("Mapped allowlist digest changed.");
        var entries = new List<MappedAllowlistEntry>(manifest.RowCount);
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            var entry = JsonSerializer.Deserialize<MappedAllowlistEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("Mapped allowlist contains an empty row.");
            if (!entry.LinkPath.StartsWith(manifest.SourceRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) || entry.DavItemId == Guid.Empty
                || (entries.Count > 0 && string.CompareOrdinal(entries[^1].LinkPath, entry.LinkPath) >= 0))
                throw new InvalidDataException("Mapped allowlist contains an invalid or duplicate path.");
            entries.Add(entry);
        }
        if (entries.Count != manifest.RowCount)
            throw new InvalidDataException("Mapped allowlist count disagrees with its manifest.");
        return entries;
    }

    private static async Task<MappedLibraryInventory> ReadShardAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var shard = await JsonSerializer.DeserializeAsync<MappedLibraryInventory>(stream, JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Mapped inventory shard is empty.");
        shard.Validate();
        return shard;
    }

    private static async Task WritePrivateJsonAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        await using var output = CanaryPackageWriter.CreatePrivateFile(path);
        await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // The shard must be durable before its manifest can be published.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false)).ToLowerInvariant();
    }
}
