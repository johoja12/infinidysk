namespace NzbDavMigration.Canary;

internal static class CanaryPathSafety
{
    public static string ResolveRoot(string path, string description)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var info = new DirectoryInfo(root);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"{description} does not exist: {root}");
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"{description} must not be a symbolic link: {root}");
        return root;
    }

    public static string ResolveBeneath(string root, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe {description} '{relativePath}'.");
        var components = relativePath.Split('/');
        if (components.Any(component => component is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe {description} '{relativePath}'.");
        var resolved = Path.GetFullPath(Path.Join(root, Path.Join(components)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"{description} escapes its root: {relativePath}");
        return resolved;
    }

    public static IReadOnlyList<string> EnsureParentDirectories(string root, string leafPath)
    {
        var created = new List<string>();
        var parent = Path.GetDirectoryName(leafPath)!;
        var relative = Path.GetRelativePath(root, parent);
        if (relative == ".")
            return created;
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Join(current, component);
            var info = new DirectoryInfo(current);
            if (info.Exists)
            {
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Canary output parent is a symbolic link: {current}");
                continue;
            }
            if (PathExistsNoFollow(current))
                throw new IOException($"Canary output parent is not a directory: {current}");
            Directory.CreateDirectory(current);
            created.Add(current);
        }
        return created;
    }

    public static void EnsureParentsExistWithoutLinks(string root, string leafPath)
    {
        var parent = Path.GetDirectoryName(leafPath)!;
        var relative = Path.GetRelativePath(root, parent);
        if (relative == ".")
            return;
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Join(current, component);
            var info = new DirectoryInfo(current);
            if (!info.Exists)
                throw new DirectoryNotFoundException($"Canary output parent disappeared: {current}");
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Canary output parent is a symbolic link: {current}");
        }
    }

    public static bool PathExistsNoFollow(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static bool IsLocalMount(string path)
    {
        var type = FindMount(path).Type;
        return !type.StartsWith("fuse", StringComparison.OrdinalIgnoreCase)
               && !type.StartsWith("nfs", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetMountPoint(string path) => FindMount(path).Mount;

    private static (string Mount, string Type) FindMount(string path)
    {
        var mountInfo = File.ReadAllLines("/proc/self/mountinfo");
        var fullPath = Path.GetFullPath(path);
        (string Mount, string Type)? best = null;
        foreach (var line in mountInfo)
        {
            var halves = line.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2)
                continue;
            var left = halves[0].Split(' ');
            var right = halves[1].Split(' ');
            if (left.Length < 5 || right.Length < 1)
                continue;
            var mount = DecodeMountInfo(left[4]);
            if (fullPath != mount && !fullPath.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal))
                continue;
            if (best is null || mount.Length > best.Value.Mount.Length)
                best = (mount, right[0]);
        }
        if (best is null)
            throw new InvalidDataException($"Unable to identify the filesystem for library root '{path}'.");
        return best.Value;
    }

    private static string DecodeMountInfo(string value)
    {
        return value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
    }
}
