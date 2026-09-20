using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbDavMigration.Export;

public sealed record CanaryExportRelease(
    string SourceReleaseId,
    Guid NzbBlobId,
    string PayloadSourcePath,
    IReadOnlyList<NzbDavExportLeaf> Leaves);

public sealed record CanaryExportRequest(
    string PackageId,
    string OutputDirectory,
    IReadOnlyList<CanaryExportRelease> Releases,
    IReadOnlyList<NzbDavSelectedLibraryLink> SelectedLinks);

public sealed partial class CanaryPackageWriter(int minimumLinks = 20, int maximumLinks = 50)
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeReleaseIdRegex();

    public async Task WriteAsync(CanaryExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Directory.Exists(request.OutputDirectory) || File.Exists(request.OutputDirectory))
            throw new IOException($"Export destination already exists: {request.OutputDirectory}");
        if (request.SelectedLinks.Count < minimumLinks || request.SelectedLinks.Count > maximumLinks)
            throw new InvalidDataException($"Selection must contain between {minimumLinks} and {maximumLinks} links.");
        RequireUnique(request.SelectedLinks.Select(link => link.LibraryRelativePath), "library path");
        RequireUnique(request.SelectedLinks.Select(link => link.LegacyDavItemId.ToString()), "legacy DavItem ID");

        var leafIds = request.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal))
            .Select(leaf => leaf.LegacyDavItemId)
            .ToHashSet();
        if (request.SelectedLinks.Any(link => !leafIds.Contains(link.LegacyDavItemId)))
            throw new InvalidDataException("Every selected link must resolve to a ready export leaf.");

        var output = Path.GetFullPath(request.OutputDirectory);
        var parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Export destination has no parent.");
        Directory.CreateDirectory(parent);
        var stage = Path.Join(parent, $".{Path.GetFileName(output)}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var committed = false;
        try
        {
            var payloads = new List<NzbDavPayloadFile>();
            var checksums = new List<NzbDavChecksumEntry>();
            var manifestReleases = new List<NzbDavExportRelease>();
            Directory.CreateDirectory(Path.Join(stage, "payloads"));
            foreach (var release in request.Releases.OrderBy(item => item.SourceReleaseId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SafeReleaseIdRegex().IsMatch(release.SourceReleaseId))
                    throw new InvalidDataException($"Unsafe source release id '{release.SourceReleaseId}'.");
                var relativePath = $"payloads/{release.SourceReleaseId}.nzb";
                var destination = Path.Join(stage, relativePath.Replace('/', Path.DirectorySeparatorChar));
                await CopyAndFlushAsync(release.PayloadSourcePath, destination, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(destination);
                var digest = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
                payloads.Add(new NzbDavPayloadFile(relativePath, info.Length, digest));
                checksums.Add(new NzbDavChecksumEntry(relativePath, digest));
                manifestReleases.Add(new NzbDavExportRelease(
                    release.SourceReleaseId, release.NzbBlobId, relativePath, release.Leaves));
            }

            var manifest = new NzbDavExportManifest(
                NzbDavExportManifest.CurrentSchemaVersion,
                request.PackageId,
                DateTimeOffset.UtcNow,
                MigrationSourceTypes.NzbDav,
                manifestReleases,
                request.SelectedLinks.OrderBy(link => link.LibraryRelativePath, StringComparer.Ordinal).ToArray(),
                payloads,
                checksums);
            var manifestPath = Path.Join(stage, "manifest.json");
            await WriteAndFlushAsync(manifestPath, NzbDavExportManifestJson.Serialize(manifest), cancellationToken)
                .ConfigureAwait(false);

            await WriteReportsAsync(stage, manifest, cancellationToken).ConfigureAwait(false);
            var checksumFiles = payloads.Select(payload => payload.RelativePath)
                .Concat(["manifest.json", "success.json", "exclusions.json"])
                .Order(StringComparer.Ordinal);
            var lines = new StringBuilder();
            foreach (var relativePath in checksumFiles)
            {
                var path = Path.Join(stage, relativePath.Replace('/', Path.DirectorySeparatorChar));
                lines.Append(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false))
                    .Append("  ").Append(relativePath).Append('\n');
            }
            await WriteAndFlushAsync(Path.Join(stage, "SHA256SUMS"), lines.ToString(), cancellationToken)
                .ConfigureAwait(false);

            Directory.Move(stage, output);
            committed = true;
        }
        finally
        {
            if (!committed && Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task WriteReportsAsync(
        string stage,
        NzbDavExportManifest manifest,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var success = manifest.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal));
        var exclusions = manifest.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => !string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal));
        await WriteAndFlushAsync(Path.Join(stage, "success.json"),
            JsonSerializer.Serialize(success, options) + "\n", cancellationToken).ConfigureAwait(false);
        await WriteAndFlushAsync(Path.Join(stage, "exclusions.json"),
            JsonSerializer.Serialize(exclusions, options) + "\n", cancellationToken).ConfigureAwait(false);
    }

    private static void RequireUnique(IEnumerable<string> values, string field)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidDataException($"Selection contains a duplicate {field}.");
    }

    private static async Task CopyAndFlushAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new FileNotFoundException("Export payload is missing.", source);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Export payload must not be a symbolic link.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Atomic export durability requires fsync before rename.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static async Task WriteAndFlushAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = Encoding.UTF8.GetBytes(content);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Atomic export durability requires fsync before rename.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
