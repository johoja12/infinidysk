using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbDavMigration.Catalogue;

public sealed record OrphanCatalogueArticle(
    string MessageId,
    int FileOrdinal,
    int SegmentOrdinal,
    long SegmentBytes);

public sealed record OrphanCatalogueBlob(
    string RelativePath,
    long Length,
    long MtimeTicks,
    string? Sha256,
    string ParseStatus,
    string? FailureClass,
    string? ReleaseDigest,
    IReadOnlyList<OrphanCatalogueArticle> Articles);

public sealed record OrphanCatalogueInputItem(
    string RelativePath,
    long Length,
    long MtimeTicks);

public sealed record OrphanCatalogueInputDocument(
    int SchemaVersion,
    IReadOnlyList<OrphanCatalogueInputItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record OrphanCatalogueState(
    string InputDigest,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long BlobCount,
    long ArticleCount);

public sealed record OrphanCatalogueSummary(
    long BlobCount,
    long ValidBlobCount,
    long FailedBlobCount,
    long ArticleCount,
    string InputDigest);

public sealed record OrphanCatalogueCompletionDocument(
    OrphanCatalogueSummary Summary,
    string Sha256)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ComputeDigest(OrphanCatalogueSummary summary)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions) + "\n";
}
