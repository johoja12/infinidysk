using System.Text;
using NzbWebDAV.Models.Nzb;

namespace NzbDavMigration.Legacy;

public sealed record ResolvedLegacyNzb(string Path, byte[] Bytes, NzbDocument Document);

public sealed class LegacyBlobResolver
{
    private readonly string _root;
    private readonly long _perFileLimit;
    private long _aggregateRemaining;

    public LegacyBlobResolver(string blobRoot, long perFileLimit = 64 * 1024 * 1024, long aggregateLimit = 1024L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRoot);
        if (perFileLimit <= 0 || aggregateLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(perFileLimit));
        _root = Path.GetFullPath(blobRoot);
        _perFileLimit = perFileLimit;
        _aggregateRemaining = aggregateLimit;
    }

    public string ResolvePath(Guid id)
    {
        var compact = id.ToString("N");
        var path = Path.GetFullPath(Path.Join(_root, compact[..2], compact.Substring(2, 2), id.ToString()));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Resolved blob path escaped the configured blob root.");
        return path;
    }

    public Task<ResolvedLegacyNzb> ReadNzbAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadNzbAsync(id, null, cancellationToken);

    public async Task<ResolvedLegacyNzb> ReadNzbAsync(Guid id, string? retainedNzb,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(id);
        if (Directory.Exists(path))
            throw new InvalidDataException("Legacy NZB blob path is a directory; it must be a regular file.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null;
             directory = directory.Parent)
        {
            if (directory.LinkTarget is not null || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidDataException("Legacy blob root and shard directories must not be symbolic links.");
            if (string.Equals(directory.FullName, _root, StringComparison.Ordinal)) break;
        }
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException($"Legacy blob '{path}' must not be a symbolic link.");
        byte[] bytes;
        if (!info.Exists)
        {
            if (string.IsNullOrWhiteSpace(retainedNzb))
                throw new FileNotFoundException("Legacy NZB blob and retained history NZB are missing.", path);
            ReserveBytes(Encoding.UTF8.GetByteCount(retainedNzb));
            bytes = Encoding.UTF8.GetBytes(retainedNzb);
        }
        else
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            ReserveBytes(length);
            bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await ValidateXmlAsync(bytes, cancellationToken).ConfigureAwait(false);
        await using var parseStream = new MemoryStream(bytes, writable: false);
        var document = await NzbDocument.LoadAsync(parseStream, cancellationToken).ConfigureAwait(false);
        return new ResolvedLegacyNzb(path, bytes, document);
    }

    private void ReserveBytes(long length)
    {
        if (length > _perFileLimit)
            throw new InvalidDataException("Legacy NZB exceeds the per-file byte ceiling.");
        if (Interlocked.Add(ref _aggregateRemaining, -length) < 0)
            throw new InvalidDataException("Legacy NZB reads exceed the aggregate byte ceiling.");
    }

    private static async Task ValidateXmlAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await NzbXmlSecurity.ValidateAsync(
            stream,
            bytes.LongLength * 4 + 1024,
            cancellationToken).ConfigureAwait(false);
    }
}
