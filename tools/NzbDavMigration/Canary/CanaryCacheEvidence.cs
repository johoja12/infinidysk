using System.Text.Json;

namespace NzbDavMigration.Canary;

public sealed record CanaryCacheEvidenceResult(
    string Label,
    long CachedBytes,
    long ExpectedBytes,
    double CoveragePercent,
    string Source)
{
    public static implicit operator string?(CanaryCacheEvidenceResult? evidence) =>
        evidence?.Label;
}

internal static class CanaryCacheEvidence
{
    public static CanaryCacheEvidenceResult? Classify(
        string? cacheRoot,
        string libraryPath,
        long expectedSize)
    {
        if (cacheRoot is null || expectedSize <= 0)
            return null;

        var root = CanaryPathSafety.ResolveRoot(cacheRoot, "Cache evidence root");
        var metadataRoot = ResolveMetadataRoot(root);
        if (metadataRoot is null)
            return null;

        var libraryInfo = new FileInfo(libraryPath);
        var resolved = libraryInfo.LinkTarget is null
            ? libraryInfo.FullName
            : libraryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        if (resolved is null)
            return null;

        var mount = CanaryPathSafety.GetMountPoint(resolved);
        var relative = Path.GetRelativePath(mount, resolved);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        var candidate = ResolveCandidate(root, relative);
        var metadataPath = ResolveCandidate(metadataRoot, relative);
        if (candidate is null || metadataPath is null)
            return null;

        var dataExists = CanaryPathSafety.PathExistsNoFollow(candidate);
        var metadataExists = CanaryPathSafety.PathExistsNoFollow(metadataPath);
        if (!dataExists && !metadataExists)
            return Result("observed-cold", 0, expectedSize, "none");
        if (!dataExists || !metadataExists)
            return null;

        var dataInfo = new FileInfo(candidate);
        var metadataInfo = new FileInfo(metadataPath);
        if (dataInfo.LinkTarget is not null || metadataInfo.LinkTarget is not null
            || !dataInfo.Exists || !metadataInfo.Exists
            || dataInfo.Attributes.HasFlag(FileAttributes.Directory)
            || metadataInfo.Attributes.HasFlag(FileAttributes.Directory)
            || dataInfo.Length != expectedSize)
            return null;

        RcloneVfsMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<RcloneVfsMetadata>(
                File.ReadAllText(metadataPath));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }

        if (metadata is null || metadata.Size != expectedSize
            || !TryCountCoveredBytes(metadata.Rs ?? [], expectedSize, out var cachedBytes))
            return null;

        var label = cachedBytes switch
        {
            0 => "observed-cold",
            var bytes when bytes == expectedSize => "observed-warm",
            _ => "observed-partial",
        };
        return Result(label, cachedBytes, expectedSize, "rclone-vfs-meta");
    }

    private static string? ResolveMetadataRoot(string cacheRoot)
    {
        var remote = new DirectoryInfo(cacheRoot);
        var vfs = remote.Parent;
        var cache = vfs?.Parent;
        if (vfs is null || cache is null
            || !string.Equals(vfs.Name, "vfs", StringComparison.Ordinal))
            return null;
        return Path.GetFullPath(Path.Join(cache.FullName, "vfsMeta", remote.Name));
    }

    private static string? ResolveCandidate(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Join(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }

    private static bool TryCountCoveredBytes(
        IReadOnlyList<RcloneVfsRange> ranges,
        long expectedSize,
        out long cachedBytes)
    {
        cachedBytes = 0;
        long? currentStart = null;
        long currentEnd = 0;
        foreach (var range in ranges.OrderBy(range => range.Pos))
        {
            if (range.Pos < 0 || range.Size < 0)
                return false;
            long end;
            try
            {
                end = checked(range.Pos + range.Size);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (end > expectedSize)
                return false;
            if (range.Size == 0)
                continue;

            if (currentStart is null)
            {
                currentStart = range.Pos;
                currentEnd = end;
            }
            else if (range.Pos <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, end);
            }
            else
            {
                cachedBytes = checked(cachedBytes + currentEnd - currentStart.Value);
                currentStart = range.Pos;
                currentEnd = end;
            }
        }

        if (currentStart is not null)
            cachedBytes = checked(cachedBytes + currentEnd - currentStart.Value);
        return cachedBytes <= expectedSize;
    }

    private static CanaryCacheEvidenceResult Result(
        string label,
        long cachedBytes,
        long expectedSize,
        string source) => new(
        label,
        cachedBytes,
        expectedSize,
        cachedBytes * 100d / expectedSize,
        source);

    private sealed record RcloneVfsMetadata(long Size, IReadOnlyList<RcloneVfsRange>? Rs);
    private sealed record RcloneVfsRange(long Pos, long Size);
}
