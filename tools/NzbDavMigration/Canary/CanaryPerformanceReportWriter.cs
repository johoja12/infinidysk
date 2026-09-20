using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NzbDavMigration.Canary;

public sealed class CanaryPerformanceReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task WriteAsync(
        string outputDirectory,
        CanaryPerformanceResults results,
        CancellationToken cancellationToken = default)
    {
        results.Metadata.Validate();
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Performance output '{outputDirectory}' already exists.");
        var destination = Path.GetFullPath(outputDirectory);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Join(parent, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(stage, "performance-results.json"),
                JsonSerializer.Serialize(results, JsonOptions) + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(stage, "performance-results.md"),
                BuildMarkdown(results), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
        }
        catch
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static string BuildMarkdown(CanaryPerformanceResults results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# NzbDav canary performance results");
        builder.AppendLine();
        builder.AppendLine($"Created: {results.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine("Frontend-proxied InfiniDysk measurements are diagnostic only.");
        builder.AppendLine();
        builder.AppendLine("| File | Representation | Size | Side/pass | Cache | Route | 10% TTFB / completion ms | 50% TTFB / completion ms | 90% TTFB / completion ms | Sequential MiB/s | Requested / actual bytes | Error / timeout |");
        builder.AppendLine("|---|---:|---:|---|---|---|---:|---:|---:|---:|---:|---|");
        var files = results.Files.ToDictionary(file => file.LibraryRelativePath, StringComparer.Ordinal);
        foreach (var group in results.Observations.GroupBy(
                     row => (row.LibraryRelativePath, row.Side, row.Pass)))
        {
            var file = files[group.Key.LibraryRelativePath];
            var rows = group.ToDictionary(row => row.Operation, StringComparer.Ordinal);
            rows.TryGetValue("sequential", out var sequential);
            var errors = group.Where(row => row.Error is not null)
                .Select(row => $"{row.Operation}: {row.Error}{(row.TimedOut ? " (timeout)" : "")}");
            var diagnostic = group.Any(row => row.DiagnosticOnly) ? " diagnostic" : "";
            builder.Append("| ").Append(Escape(file.LibraryRelativePath))
                .Append(" | ").Append(file.Representation)
                .Append(" | ").Append(file.ExpectedFileSize.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(group.Key.Side).Append('/').Append(group.Key.Pass)
                .Append(" | ").Append(group.First().CacheLabel)
                .Append(" | ").Append(Escape(group.First().Route.EffectiveWebDavUrl))
                .Append(" (").Append(group.First().Route.RouteKind).Append(diagnostic).Append(')')
                .Append(" | ").Append(Timing(rows, "seek-10"))
                .Append(" | ").Append(Timing(rows, "seek-50"))
                .Append(" | ").Append(Timing(rows, "seek-90"))
                .Append(" | ").Append(Number(sequential?.MebibytesPerSecond))
                .Append(" | ").Append(sequential is null ? "—" : $"{sequential.RequestedBytes} / {sequential.ActualBytes}")
                .Append(" | ").Append(Escape(string.Join("; ", errors.DefaultIfEmpty("—"))))
                .AppendLine(" |");
        }
        return builder.ToString();
    }

    private static string Timing(Dictionary<string, CanaryPerformanceObservation> rows, string key) =>
        rows.TryGetValue(key, out var row)
            ? $"{Number(row.TimeToFirstByteMilliseconds)} / {Number(row.DurationMilliseconds)}"
            : "—";

    private static string Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
