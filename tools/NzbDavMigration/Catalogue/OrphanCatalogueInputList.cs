using System.Security.Cryptography;
using System.Text.Json;
using NzbDavMigration.Export;

namespace NzbDavMigration.Catalogue;

public static class OrphanCatalogueInputList
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task CreateAsync(
        string blobRoot,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(blobRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Blob root does not exist.");
        if (IsLink(new DirectoryInfo(root)))
            throw new InvalidDataException("Blob root must not be a symbolic link.");
        var items = new List<OrphanCatalogueInputItem>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLink(directory))
                continue;
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo child)
                {
                    if (!IsLink(child)) pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                file.Refresh();
                var relativePath = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                long length;
                try { length = file.Length; }
                catch (IOException) { length = 0; }
                items.Add(new OrphanCatalogueInputItem(relativePath, length, file.LastWriteTimeUtc.Ticks));
            }
        }

        var document = new OrphanCatalogueInputDocument(
            OrphanCatalogueInputDocument.CurrentSchemaVersion,
            items.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
        await using var output = CanaryPackageWriter.CreatePrivateFile(outputPath);
        await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Frozen inventory must be durable before scanning.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    public static async Task<(OrphanCatalogueInputDocument Document, string Sha256)> ReadAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var document = JsonSerializer.Deserialize<OrphanCatalogueInputDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Frozen catalogue input is empty.");
        if (document.SchemaVersion != OrphanCatalogueInputDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported frozen catalogue input schema.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Items)
        {
            RequireSafeRelativePath(item.RelativePath);
            if (item.Length < 0 || item.MtimeTicks < 0)
                throw new InvalidDataException("Frozen catalogue input contains invalid file metadata.");
            if (!paths.Add(item.RelativePath))
                throw new InvalidDataException("Frozen catalogue input contains a duplicate relative path.");
        }
        return (document, digest);
    }

    internal static string ResolveSafePath(string root, string relativePath)
    {
        RequireSafeRelativePath(relativePath);
        var normalizedRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Join(normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Frozen catalogue path escaped the blob root.");
        return path;
    }

    private static void RequireSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Frozen catalogue input contains an unsafe relative path.");
        var components = relativePath.Replace('\\', '/').Split('/');
        if (components.Any(component => component is "" or "." or ".."))
            throw new InvalidDataException("Frozen catalogue input contains an unsafe relative path.");
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
