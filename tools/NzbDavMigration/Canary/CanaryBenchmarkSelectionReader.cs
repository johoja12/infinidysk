using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbDavMigration.Canary;

public static class CanaryBenchmarkSelectionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<CanaryBenchmarkSelection> ReadAndValidateAsync(
        string selectionPath,
        string planPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(selectionPath);
        var selection = await JsonSerializer.DeserializeAsync<CanaryBenchmarkSelection>(
                stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
                        ?? throw new InvalidDataException("Benchmark selection is empty.");
        var plan = await CanaryPlanVerifier.ReadAsync(planPath, cancellationToken).ConfigureAwait(false);
        ValidateAgainstPlan(selection, plan.Plan);
        return selection;
    }

    public static void ValidateAgainstPlan(CanaryBenchmarkSelection selection, NzbDavCanaryPlan plan)
    {
        selection.Validate();
        foreach (var file in selection.Files)
        {
            var match = plan.Links.SingleOrDefault(link =>
                link.LibraryRelativePath == file.LibraryRelativePath);
            if (match is null
                || match.CorrelationStatus != "exact"
                || match.NewRelativeTarget is null
                || match.LegacyDavItemId != file.LegacyDavItemId
                || match.ExpectedFileSize != file.ExpectedFileSize
                || !Guid.TryParse(Path.GetFileName(match.NewRelativeTarget), out var targetId)
                || targetId != file.InfiniDyskDavItemId)
                throw new InvalidDataException(
                    $"Benchmark file '{file.LibraryRelativePath}' is missing or not exactly correlated by the plan.");
        }
    }
}
