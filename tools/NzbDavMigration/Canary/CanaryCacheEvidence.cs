namespace NzbDavMigration.Canary;

internal static class CanaryCacheEvidence
{
    public static string? Classify(
        string? cacheRoot,
        string libraryPath,
        long expectedSize)
    {
        if (cacheRoot is null)
            return null;
        var root = CanaryPathSafety.ResolveRoot(cacheRoot, "Cache evidence root");
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
        var candidate = Path.GetFullPath(Path.Join(root, relative));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        var info = new FileInfo(candidate);
        if (!CanaryPathSafety.PathExistsNoFollow(candidate))
            return "observed-cold";
        if (info.LinkTarget is null && info.Exists && info.Length == expectedSize)
            return "observed-warm";
        return null;
    }
}
