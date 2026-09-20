using System.Xml;
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

    public async Task<ResolvedLegacyNzb> ReadNzbAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(id);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Legacy NZB blob was not found.", path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Legacy blob '{path}' must not be a symbolic link.");
        if (info.Length > _perFileLimit)
            throw new InvalidDataException($"Legacy blob '{path}' exceeds the per-file byte ceiling.");
        var remaining = Interlocked.Add(ref _aggregateRemaining, -info.Length);
        if (remaining < 0)
            throw new InvalidDataException("Legacy blob reads exceed the aggregate byte ceiling.");

        byte[] bytes;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[info.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await ValidateXmlAsync(bytes, cancellationToken).ConfigureAwait(false);
        await using var parseStream = new MemoryStream(bytes, writable: false);
        var document = await NzbDocument.LoadAsync(parseStream, cancellationToken).ConfigureAwait(false);
        return new ResolvedLegacyNzb(path, bytes, document);
    }

    private static async Task ValidateXmlAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = bytes.LongLength * 4 + 1024,
        };
        await using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
            cancellationToken.ThrowIfCancellationRequested();
    }
}
