using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.UsenetMigration.NzbDav;

public readonly record struct NzbDavArticleSegment(int? Number, long Bytes, string MessageId);

public static class NzbDavArticleIdentity
{
    public const string DirectKind = "direct-articles-v1";
    public const string ArchiveMemberKind = "archive-member-v1";

    public static string ComputeDirect(IEnumerable<NzbDavArticleSegment> segments) =>
        Compute("nzbdav-direct-v1", hash => AppendSegments(hash, segments));

    public static string ComputeRelease(IEnumerable<IEnumerable<NzbDavArticleSegment>> files) =>
        Compute("nzbdav-release-v1", hash =>
        {
            var materialized = files.Select(file => file.ToArray()).ToArray();
            if (materialized.Length == 0)
                throw new InvalidDataException("A release identity requires at least one NZB file.");
            AppendInt32(hash, materialized.Length);
            foreach (var file in materialized)
            {
                AppendString(hash, "file");
                AppendSegments(hash, file);
            }
        });

    public static string ComputeArchiveMember(string releaseDigest, string innerPath, long fileSize)
    {
        RequireDigest(releaseDigest);
        if (fileSize < 0)
            throw new InvalidDataException("An archive member identity cannot use a negative size.");
        var normalizedPath = NormalizeInnerPath(innerPath);
        return Compute("nzbdav-archive-member-v1", hash =>
        {
            AppendString(hash, releaseDigest);
            AppendString(hash, normalizedPath);
            AppendInt64(hash, fileSize);
        });
    }

    private static void AppendSegments(IncrementalHash hash, IEnumerable<NzbDavArticleSegment> segments)
    {
        var materialized = segments.ToArray();
        if (materialized.Length == 0)
            throw new InvalidDataException("An article identity requires at least one segment.");
        AppendInt32(hash, materialized.Length);
        foreach (var segment in materialized)
        {
            if (segment.Bytes < 0)
                throw new InvalidDataException("An article identity cannot use a negative segment size.");
            var messageId = NormalizeMessageId(segment.MessageId);
            AppendInt32(hash, segment.Number ?? -1);
            AppendInt64(hash, segment.Bytes);
            AppendString(hash, messageId);
        }
    }

    private static string Compute(string domain, Action<IncrementalHash> append)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, domain);
        append(hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeMessageId(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('<') && normalized.EndsWith('>') && normalized.Length >= 2)
            normalized = normalized[1..^1].Trim();
        if (normalized.Length == 0)
            throw new InvalidDataException("An article identity contains an empty message id.");
        return normalized;
    }

    private static string NormalizeInnerPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.StartsWith('\\'))
            throw new InvalidDataException($"Unsafe archive member path '{value}'.");
        var components = value.Replace('\\', '/').Split('/');
        if (components.Any(component => component is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe archive member path '{value}'.");
        return string.Join('/', components);
    }

    private static void RequireDigest(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("Release identity digest must be lowercase SHA-256 hex.");
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
