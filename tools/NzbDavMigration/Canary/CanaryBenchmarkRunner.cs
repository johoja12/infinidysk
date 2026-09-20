namespace NzbDavMigration.Canary;

public sealed class CanaryBenchmarkRunner
{
    private readonly CanaryPerformanceProbe _probe = new();

    public async Task<CanaryPerformanceResults> RunAsync(
        CanaryBenchmarkSelection selection,
        CanaryBenchmarkMetadata metadata,
        string legacyRoot,
        string infiniDyskRoot,
        TimeSpan timeout,
        string? legacyCacheRoot = null,
        string? infiniDyskCacheRoot = null,
        CancellationToken cancellationToken = default)
    {
        selection.Validate();
        metadata.Validate();
        var legacy = CanaryPathSafety.ResolveRoot(legacyRoot, "Legacy library root");
        var infini = CanaryPathSafety.ResolveRoot(infiniDyskRoot, "InfiniDysk canary library root");
        var paths = selection.Files.ToDictionary(
            file => file.LibraryRelativePath,
            file => (
                Legacy: VerifyFile(legacy, file),
                InfiniDysk: VerifyFile(infini, file)),
            StringComparer.Ordinal);
        var observations = new List<CanaryPerformanceObservation>();
        foreach (var pass in new[] { "first-pass", "repeat-pass" })
        {
            foreach (var file in selection.Files)
            {
                var pair = paths[file.LibraryRelativePath];
                var legacyCache = CanaryCacheEvidence.Classify(
                    legacyCacheRoot, pair.Legacy, file.ExpectedFileSize);
                var infiniCache = CanaryCacheEvidence.Classify(
                    infiniDyskCacheRoot, pair.InfiniDysk, file.ExpectedFileSize);
                observations.AddRange(await _probe.ProbeAsync(
                    file, "legacy", pass, metadata.LegacyRoute,
                    _ => File.Open(pair.Legacy, FileMode.Open, FileAccess.Read, FileShare.Read),
                    timeout, legacyCache, cancellationToken).ConfigureAwait(false));
                observations.AddRange(await _probe.ProbeAsync(
                    file, "infinidysk", pass, metadata.InfiniDyskRoute,
                    _ => File.Open(pair.InfiniDysk, FileMode.Open, FileAccess.Read, FileShare.Read),
                    timeout, infiniCache, cancellationToken).ConfigureAwait(false));
            }
        }
        return new CanaryPerformanceResults(DateTimeOffset.UtcNow, metadata, selection.Files, observations);
    }

    private static string VerifyFile(string root, CanaryBenchmarkFile file)
    {
        var path = CanaryPathSafety.ResolveBeneath(root, file.LibraryRelativePath, "benchmark library path");
        if (!CanaryPathSafety.PathExistsNoFollow(path))
            throw new FileNotFoundException("Benchmark library entry is missing.", path);
        var info = new FileInfo(path);
        FileInfo target;
        if (info.LinkTarget is not null)
            target = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo
                     ?? throw new InvalidDataException($"Benchmark path '{path}' does not resolve to a regular file.");
        else
            target = info;
        if (!target.Exists || target.Attributes.HasFlag(FileAttributes.Directory))
            throw new InvalidDataException($"Benchmark path '{path}' does not resolve to a readable regular file.");
        if (target.Length != file.ExpectedFileSize)
            throw new InvalidDataException(
                $"Benchmark path '{path}' has size {target.Length}, expected {file.ExpectedFileSize}.");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
        }
        return path;
    }
}
