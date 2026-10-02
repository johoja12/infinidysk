using System.Runtime.CompilerServices;

namespace NzbWebDAV.Database;

/// <summary>
/// On-disk blob storage under <c>CONFIG_PATH/blobs</c>. One DI singleton owns
/// the cache and filesystem; call sites should prefer this over the static
/// <see cref="BlobStore"/> facade.
/// </summary>
public interface IBlobStore
{
    [OverloadResolutionPriority(1)]
    Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default);
    Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a typed blob without publishing a content revision, so active readers
    /// keep streaming. Only for rewrites that leave the decoded media bytes unchanged
    /// (lazy RAR volume resolution). Stores that cannot honor this fail closed by
    /// publishing like <see cref="WriteBlob{T}(Guid, T, CancellationToken)"/>.
    /// </summary>
    Task WriteContentPreservingBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default)
        => WriteBlob(id, blob, cancellationToken);
    Stream? ReadBlob(Guid id);
    Task<T?> ReadBlob<T>(Guid id);
    bool Exists(Guid id);
    bool Delete(Guid id);
}
