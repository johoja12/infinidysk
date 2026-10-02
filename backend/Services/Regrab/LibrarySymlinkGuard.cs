using NzbWebDAV.Config;

namespace NzbWebDAV.Services.Regrab;

public enum LibraryLinkInspection
{
    /// <summary>The path is a symlink under a configured library root and passes every guard.</summary>
    Symlink,
    /// <summary>Nothing exists at the path.</summary>
    Missing,
    /// <summary>A regular file or directory exists at the path. Regrab never deletes these.</summary>
    NotSymlink,
    /// <summary>The path is not beneath any configured library root.</summary>
    OutsideRoots,
    /// <summary>The root or a parent directory below it is itself a symlink.</summary>
    SymlinkedParent,
    /// <summary>The symlink exists but no longer points at the expected target.</summary>
    TargetMismatch,
    /// <summary>The path is relative or not normalized (for example it contains <c>..</c>).</summary>
    InvalidPath,
}

public sealed record LibraryLinkCheck(
    LibraryLinkInspection Kind,
    string Path,
    string? Root,
    string? Target,
    string Message);

/// <summary>
/// Guards the only filesystem mutation regrab performs: removing one library symlink.
/// The link must sit beneath a configured library root (<c>media.library-dir</c> or a
/// <c>media.library-scan-dirs</c> entry), be reached without traversing a symlinked
/// directory, be a symlink itself (inspected without following it), and, when the
/// caller knows the expected target, still point at it. The symlink target is never
/// opened, followed, or deleted.
/// </summary>
public static class LibrarySymlinkGuard
{
    /// <summary>
    /// Roots regrab may remove symlinks beneath: only the primary Library Directory
    /// (<c>media.library-dir</c>, the parent of the Sonarr/Radarr roots). Additional
    /// <c>media.library-scan-dirs</c> stay read-only scan sources.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredRoots(ConfigManager config)
    {
        var libraryDir = config.GetLibraryDir();
        return string.IsNullOrWhiteSpace(libraryDir) ? [] : NormalizeRoots([libraryDir]);
    }

    public static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> roots) =>
        roots
            .Where(root => !string.IsNullOrWhiteSpace(root) && System.IO.Path.IsPathRooted(root))
            .Select(root => TrimSeparators(System.IO.Path.GetFullPath(root)))
            .Where(root => root.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Inspects <paramref name="path"/> without following symlinks.
    /// </summary>
    /// <param name="expectedTarget">When set, the symlink must point exactly here.</param>
    /// <param name="expectedLegacyId">
    /// When set (and <paramref name="expectedTarget"/> is not), the symlink must point at a
    /// <c>.ids</c> target whose final segment is this DavItem id.
    /// </param>
    public static LibraryLinkCheck Inspect(
        string path,
        IReadOnlyList<string> roots,
        string? expectedTarget = null,
        Guid? expectedLegacyId = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
            return new(LibraryLinkInspection.InvalidPath, path, null, null,
                "The library path must be absolute.");
        var normalized = System.IO.Path.GetFullPath(path);
        if (!string.Equals(normalized, path, StringComparison.Ordinal))
            return new(LibraryLinkInspection.InvalidPath, path, null, null,
                "The library path is not normalized; refusing to touch it.");

        var root = roots
            .Where(candidate => path.StartsWith(candidate + System.IO.Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        if (root is null)
            return new(LibraryLinkInspection.OutsideRoots, path, null, null,
                "The library path is outside the Library Directory; regrab only removes links there.");

        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.LinkTarget is not null)
            return new(LibraryLinkInspection.SymlinkedParent, path, root, null,
                $"The library directory {root} is itself a symlink; refusing to remove links through it.");
        if (!rootInfo.Exists)
            return new(LibraryLinkInspection.Missing, path, root, null,
                $"The library directory {root} is not available.");

        // Walk every directory between the root and the entry without following links.
        var relative = path[(root.Length + 1)..];
        var segments = relative.Split(System.IO.Path.DirectorySeparatorChar);
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = System.IO.Path.Join(current, segments[index]);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
                return new(LibraryLinkInspection.SymlinkedParent, path, root, null,
                    $"The library folder {current} is a symlink; refusing to remove links through it.");
            if (!directory.Exists)
                return new(LibraryLinkInspection.Missing, path, root, null,
                    "The library link no longer exists.");
        }

        var entry = new FileInfo(path);
        var target = entry.LinkTarget;
        if (target is null)
        {
            return File.Exists(path) || Directory.Exists(path)
                ? new(LibraryLinkInspection.NotSymlink, path, root, null,
                    "The library entry is a regular file or folder, not a symlink. Regrab only removes symlinks.")
                : new(LibraryLinkInspection.Missing, path, root, null,
                    "The library link no longer exists.");
        }

        if (expectedTarget is not null && !string.Equals(target, expectedTarget, StringComparison.Ordinal))
            return new(LibraryLinkInspection.TargetMismatch, path, root, target,
                "The library link now points somewhere else; it may already have been replaced.");
        if (expectedTarget is null && expectedLegacyId is { } legacyId && !TargetsDavItem(target, legacyId))
            return new(LibraryLinkInspection.TargetMismatch, path, root, target,
                "The library link no longer points at the failed legacy item; it may already have been replaced.");

        return new(LibraryLinkInspection.Symlink, path, root, target, "The library link is a symlink.");
    }

    /// <summary>True when <paramref name="target"/> is a <c>.ids</c> path ending with <paramref name="davItemId"/>.</summary>
    public static bool TargetsDavItem(string target, Guid davItemId)
    {
        var normalized = target.Replace('\\', '/');
        if (!normalized.Contains("/.ids/", StringComparison.Ordinal))
            return false;
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dot = leaf.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
            leaf = leaf[..dot];
        return Guid.TryParse(leaf, out var parsed) && parsed == davItemId;
    }

    /// <summary>
    /// Re-inspects the link and removes only the symlink itself. Returns the removed link's
    /// target. Throws <see cref="InvalidOperationException"/> when any guard no longer holds.
    /// </summary>
    public static string RemoveSymlink(
        string path,
        IReadOnlyList<string> roots,
        string? expectedTarget,
        Guid? expectedLegacyId)
    {
        var check = Inspect(path, roots, expectedTarget, expectedLegacyId);
        if (check.Kind != LibraryLinkInspection.Symlink)
            throw new InvalidOperationException(check.Message);
        // unlink(2) removes the directory entry only; a symlink target is never followed.
        File.Delete(path);
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("The library symlink is still present after removal.");
        return check.Target!;
    }

    private static string TrimSeparators(string path) =>
        path.Length > 1 ? path.TrimEnd(System.IO.Path.DirectorySeparatorChar) : path;
}
