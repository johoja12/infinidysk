using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.UsenetMigration.Canary;

public sealed class NzbDavCanaryPlanWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string GetPlanDirectory(string outputRoot, long runId, string sourcePackageDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackageDigest);
        return Path.Join(
            Path.GetFullPath(outputRoot),
            $"run-{runId:D10}-{sourcePackageDigest[..12]}");
    }

    public async Task<string> WriteAsync(
        string outputRoot,
        NzbDavCanaryPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        var directoryName = $"run-{plan.RunId:D10}-{plan.SourcePackageDigest[..12]}";
        var destination = GetPlanDirectory(root, plan.RunId, plan.SourcePackageDigest);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Canary plan '{destination}' already exists and will not be overwritten.");

        var staging = Path.Join(root, $".{directoryName}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var planBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, JsonOptions) + "\n");
            await File.WriteAllBytesAsync(Path.Join(staging, "plan.json"), planBytes, cancellationToken)
                .ConfigureAwait(false);
            var digest = Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant();
            await File.WriteAllTextAsync(
                    Path.Join(staging, "SHA256SUMS"),
                    $"{digest}  plan.json\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(staging, destination);
            return destination;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }
}
