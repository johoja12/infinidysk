using NzbWebDAV.UsenetMigration.Naming;

namespace NzbDavMigration.Export;

public sealed record NzbDavSourceNames(string FileName, string JobName);

public static class NzbDavSourceNameResolver
{
    public static NzbDavSourceNames FromHistory(string? fileName, string? jobName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(jobName))
            throw new InvalidDataException("Legacy history filename and job name are required.");
        if (fileName is "." or ".." || fileName.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException($"Unsafe legacy history filename '{fileName}'.");
        if (!string.Equals(NzbDavNaming.JobName(fileName), jobName, StringComparison.Ordinal))
            throw new InvalidDataException("Legacy history filename and job name disagree.");
        return new NzbDavSourceNames(fileName, jobName);
    }

    public static NzbDavSourceNames FromLegacyPaths(IEnumerable<string> legacyPaths)
    {
        ArgumentNullException.ThrowIfNull(legacyPaths);
        var paths = legacyPaths.ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("Recovered release has no legacy content paths.");

        var jobNames = paths.Select(path =>
        {
            var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!path.StartsWith("/content/", StringComparison.Ordinal)
                || components.Length < 4
                || !string.Equals(components[0], "content", StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected legacy content path '{path}'.");
            return components[2];
        }).Distinct(StringComparer.Ordinal).ToArray();

        if (jobNames.Length != 1 || string.IsNullOrWhiteSpace(jobNames[0]))
            throw new InvalidDataException("Recovered release does not have one common legacy job name.");

        var fileName = $"{jobNames[0]}.nzb";
        var jobName = NzbDavNaming.JobName(fileName);
        if (!string.Equals(jobName, jobNames[0], StringComparison.Ordinal))
            throw new InvalidDataException("Recovered legacy job name is not a canonical NzbDav path component.");
        return new NzbDavSourceNames(fileName, jobName);
    }
}
