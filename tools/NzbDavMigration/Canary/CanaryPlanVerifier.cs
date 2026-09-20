using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbDavMigration.Canary;

internal sealed record VerifiedCanaryPlan(NzbDavCanaryPlan Plan, string PlanSha256);

internal static class CanaryPlanVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<VerifiedCanaryPlan> ReadAsync(
        string planPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(planPath);
        if (!File.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null)
            throw new FileNotFoundException("Canary plan must be a regular file.", fullPath);
        var sumsPath = Path.Join(Path.GetDirectoryName(fullPath)!, "SHA256SUMS");
        if (!File.Exists(sumsPath) || new FileInfo(sumsPath).LinkTarget is not null)
            throw new FileNotFoundException("Canary plan checksum inventory is missing.", sumsPath);
        var expected = ParsePlanDigest(await File.ReadAllTextAsync(sumsPath, cancellationToken)
            .ConfigureAwait(false));
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException("Canary plan checksum mismatch.");
        NzbDavCanaryPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<NzbDavCanaryPlan>(bytes, JsonOptions)
                   ?? throw new InvalidDataException("Canary plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Canary plan JSON is invalid.", exception);
        }
        if (plan.SchemaVersion != NzbDavCanaryPlan.CurrentSchemaVersion || !plan.IsValid)
            throw new InvalidDataException("Canary plan is unsupported or invalid.");
        if (plan.SelectedCount != plan.Links.Count
            || plan.ActionableCount != plan.Links.Count(link => link.NewRelativeTarget is not null))
            throw new InvalidDataException("Canary plan counts do not match its link inventory.");
        return new VerifiedCanaryPlan(plan, actual);
    }

    private static string ParsePlanDigest(string sums)
    {
        var matches = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.EndsWith("  plan.json", StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || matches[0].Length != 64 + 2 + "plan.json".Length)
            throw new InvalidDataException("SHA256SUMS must contain exactly one plan.json entry.");
        var digest = matches[0][..64];
        if (digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("SHA256SUMS contains an invalid plan digest.");
        return digest;
    }
}
