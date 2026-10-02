using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.UsenetMigration.Provenance;

/// <summary>Verifies scan exclusions before leaving selected links out of a plan or acknowledgement.</summary>
internal static class NzbDavScanExclusion
{
    internal static async Task<IReadOnlySet<string>> VerifiedSourceIdsAsync(
        UsenetMigrationDbContext context, string packageDigest, CancellationToken ct)
    {
        var files = await context.ReleaseFiles.AsNoTracking()
            .Where(file => file.FileStatus == "scan-excluded").ToListAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var release = await context.Releases.AsNoTracking()
                .SingleOrDefaultAsync(item => item.StoreRef == file.StoreRef, ct).ConfigureAwait(false);
            if (file.NewDavItemId is not null || !Guid.TryParse(file.SourceFileId, out _)
                || release is not { Verdict: "red" }
                || !HasEvidence(file.Flags, release.VerdictReasons, packageDigest)
                || await context.Submissions.AnyAsync(item => item.StoreRef == file.StoreRef, ct).ConfigureAwait(false)
                || await context.MigratedReleases.AnyAsync(
                    item => item.SourceType == MigrationSourceTypes.NzbDav && item.SourceReleaseId == file.StoreRef,
                    ct).ConfigureAwait(false))
                throw new ArgumentException($"Scan-excluded source '{file.SourceFileId}' lacks consistent exclusion evidence.");
            ids.Add(file.SourceFileId!);
        }
        return ids;
    }

    private static bool HasEvidence(string? flags, string reasons, string packageDigest)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return false;
        try
        {
            var expected = JsonSerializer.Deserialize<string[]>(reasons);
            using var document = JsonDocument.Parse(flags);
            var root = document.RootElement;
            return expected is { Length: > 0 }
                   && root.TryGetProperty("packageSha256", out var digest)
                   && digest.GetString() == packageDigest
                   && root.TryGetProperty("correlation", out var correlation)
                   && correlation.TryGetProperty("method", out var method)
                   && method.GetString() == "scan-exclusion"
                   && correlation.TryGetProperty("reasons", out var recorded)
                   && recorded.Deserialize<string[]>() is { } actual
                   && expected.SequenceEqual(actual, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
