using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.UsenetMigration.Source;

public sealed record NzbDavVerifiedPackage(
    string RootPath,
    NzbDavExportManifest Manifest,
    string PackageDigest,
    IReadOnlyDictionary<string, string> PayloadPaths);

public sealed class NzbDavPackageReader
{
    public async Task<NzbDavVerifiedPackage> ReadAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = new DirectoryInfo(Path.GetFullPath(packageRoot));
        if (!root.Exists)
            throw new DirectoryNotFoundException(root.FullName);
        RejectLink(root, "package root");

        var sumsPath = Resolve(root.FullName, "SHA256SUMS");
        var manifestPath = Resolve(root.FullName, "manifest.json");
        RejectLink(new FileInfo(sumsPath), "checksum inventory");
        RejectLink(new FileInfo(manifestPath), "manifest");
        var sumsBytes = await File.ReadAllBytesAsync(sumsPath, cancellationToken).ConfigureAwait(false);
        var checksums = ParseChecksums(Encoding.UTF8.GetString(sumsBytes));
        if (!checksums.ContainsKey("manifest.json"))
            throw new InvalidDataException("SHA256SUMS does not cover manifest.json.");

        foreach (var (relativePath, expected) in checksums)
        {
            var path = Resolve(root.FullName, relativePath);
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new InvalidDataException($"Checksummed package file '{relativePath}' is missing.");
            RejectLink(info, relativePath);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException($"Checksum mismatch for '{relativePath}'.");
        }

        var manifest = NzbDavExportManifestJson.Deserialize(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        ValidateRelationships(manifest, checksums);
        var payloadPaths = manifest.Payloads.ToDictionary(
            payload => payload.RelativePath,
            payload => Resolve(root.FullName, payload.RelativePath),
            StringComparer.Ordinal);
        var packageDigest = Convert.ToHexString(SHA256.HashData(sumsBytes)).ToLowerInvariant();
        return new NzbDavVerifiedPackage(root.FullName, manifest, packageDigest, payloadPaths);
    }

    private static Dictionary<string, string> ParseChecksums(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
                throw new InvalidDataException("SHA256SUMS contains a malformed line.");
            var digest = line[..separator];
            var relativePath = line[(separator + 2)..].TrimEnd('\r');
            NzbDavExportManifest.RequirePackagePath(relativePath, "checksum path");
            if (digest.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new InvalidDataException("SHA256SUMS contains an invalid digest.");
            if (!result.TryAdd(relativePath, digest))
                throw new InvalidDataException($"SHA256SUMS contains duplicate path '{relativePath}'.");
        }
        return result;
    }

    private static void ValidateRelationships(
        NzbDavExportManifest manifest,
        Dictionary<string, string> checksums)
    {
        var payloads = manifest.Payloads.ToDictionary(payload => payload.RelativePath, StringComparer.Ordinal);
        var legacyIds = new HashSet<Guid>();
        foreach (var release in manifest.Releases)
        {
            if (!payloads.TryGetValue(release.PayloadPath, out var payload))
                throw new InvalidDataException($"Release '{release.SourceReleaseId}' references a missing payload.");
            if (!checksums.TryGetValue(payload.RelativePath, out var digest)
                || !string.Equals(digest, payload.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Payload checksum metadata disagrees for '{payload.RelativePath}'.");
            foreach (var leaf in release.Leaves)
            {
                if (!legacyIds.Add(leaf.LegacyDavItemId))
                    throw new InvalidDataException($"Duplicate legacy DavItem ID '{leaf.LegacyDavItemId}'.");
                if (!string.Equals(leaf.ParentReleaseId, release.SourceReleaseId, StringComparison.Ordinal))
                    throw new InvalidDataException($"Leaf '{leaf.LegacyDavItemId}' has inconsistent release membership.");
                if (leaf.NzbBlobId != release.NzbBlobId)
                    throw new InvalidDataException($"Leaf '{leaf.LegacyDavItemId}' has inconsistent NZB identity.");
            }
        }

        foreach (var link in manifest.SelectedLinks)
        {
            if (!legacyIds.Contains(link.LegacyDavItemId))
                throw new InvalidDataException($"Selected link '{link.LibraryRelativePath}' has no exported leaf.");
        }
    }

    private static string Resolve(string root, string relativePath)
    {
        NzbDavExportManifest.RequirePackagePath(relativePath, "package path");
        var resolved = Path.GetFullPath(Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Package path '{relativePath}' escapes its root.");
        return resolved;
    }

    private static void RejectLink(FileSystemInfo info, string description)
    {
        info.Refresh();
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The {description} must not be a symbolic link.");
    }
}
