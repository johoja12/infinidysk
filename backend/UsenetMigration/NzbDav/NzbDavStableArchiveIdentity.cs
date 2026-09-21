namespace NzbWebDAV.UsenetMigration.NzbDav;

public sealed record NzbDavArchivePartIdentity(
    IReadOnlyList<NzbDavArticleSegment> Segments,
    long SegmentStart,
    long SegmentLength,
    long FileStart,
    long FileLength);

public static class NzbDavStableArchiveIdentity
{
    public const string Kind = "archive-articles-v2";

    public static string Compute(
        string releaseDigest,
        IReadOnlyList<NzbDavArchivePartIdentity> parts,
        long fileSize) =>
        NzbDavArticleIdentity.ComputeArchiveArticles(releaseDigest, parts, fileSize);
}
