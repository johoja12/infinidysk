namespace NzbDavMigration.Canary;

public sealed record CanaryBenchmarkFile(
    string LibraryRelativePath,
    Guid LegacyDavItemId,
    Guid InfiniDyskDavItemId,
    long ExpectedFileSize,
    string Representation,
    string ResolutionClass,
    bool IsLargeFileCase);

public sealed record CanaryBenchmarkSelection(
    int SchemaVersion,
    string SelectionMode,
    IReadOnlyList<CanaryBenchmarkFile> Files)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || SelectionMode != "reviewed-manual")
            throw new InvalidDataException("Benchmark selection must be schema 1 and reviewed-manual.");
        if (Files.Count != 6)
            throw new InvalidDataException("Benchmark selection must contain exactly six files.");
        RequireUnique(Files.Select(file => file.LibraryRelativePath), "library-relative path");
        RequireUnique(Files.Select(file => file.LegacyDavItemId), "legacy DavItem ID");
        RequireUnique(Files.Select(file => file.InfiniDyskDavItemId), "InfiniDysk DavItem ID");
        if (Files.Count(file => file.IsLargeFileCase) != 1)
            throw new InvalidDataException("Benchmark selection must identify exactly one required large-file case.");
        foreach (var file in Files)
        {
            if (string.IsNullOrWhiteSpace(file.LibraryRelativePath) || Path.IsPathRooted(file.LibraryRelativePath)
                || file.LibraryRelativePath.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Benchmark selection contains an unsafe library path.");
            if (file.LegacyDavItemId == Guid.Empty || file.InfiniDyskDavItemId == Guid.Empty
                || file.ExpectedFileSize <= 0)
                throw new InvalidDataException("Benchmark selection contains missing identity or size evidence.");
            if (file.Representation is not ("direct" or "rar-multipart"))
                throw new InvalidDataException("Benchmark representation must be direct or rar-multipart.");
            if (string.IsNullOrWhiteSpace(file.ResolutionClass))
                throw new InvalidDataException("Benchmark resolution class is required.");
        }
    }

    private static void RequireUnique<T>(IEnumerable<T> values, string field) where T : notnull
    {
        var materialized = values.ToArray();
        if (materialized.Distinct().Count() != materialized.Length)
            throw new InvalidDataException($"Benchmark selection contains a duplicate {field}.");
    }
}

public sealed record CanaryRouteDescription(string EffectiveWebDavUrl, string RouteKind)
{
    public void Validate()
    {
        if (!Uri.TryCreate(EffectiveWebDavUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Route requires an absolute HTTP(S) WebDAV URL including its port.");
        if (uri.IsDefaultPort)
            throw new InvalidDataException("Route WebDAV URL must include the effective port.");
        if (RouteKind is not ("direct-backend" or "frontend-proxied"))
            throw new InvalidDataException("Route kind must be direct-backend or frontend-proxied.");
    }
}

public sealed record CanaryBenchmarkMetadata(
    CanaryRouteDescription LegacyRoute,
    CanaryRouteDescription InfiniDyskRoute)
{
    public void Validate()
    {
        LegacyRoute.Validate();
        InfiniDyskRoute.Validate();
    }
}

public sealed record CanaryPerformanceObservation(
    string LibraryRelativePath,
    string Side,
    string Pass,
    string Operation,
    long Offset,
    long RequestedBytes,
    long ActualBytes,
    double? TimeToFirstByteMilliseconds,
    double DurationMilliseconds,
    double? MebibytesPerSecond,
    string CacheLabel,
    CanaryRouteDescription Route,
    bool DiagnosticOnly,
    bool TimedOut,
    string? Error);

public sealed record CanaryPerformanceResults(
    DateTimeOffset CreatedAt,
    CanaryBenchmarkMetadata Metadata,
    IReadOnlyList<CanaryBenchmarkFile> Files,
    IReadOnlyList<CanaryPerformanceObservation> Observations);
