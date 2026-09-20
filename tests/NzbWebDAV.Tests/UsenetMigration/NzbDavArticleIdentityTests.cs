using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavArticleIdentityTests
{
    [Fact]
    public void DirectDigest_IsStableAcrossMessageIdBracketsAndWhitespace()
    {
        NzbDavArticleSegment[] canonical =
        [
            new(1, 100, "first@test"),
            new(2, 120, "second@test"),
        ];
        NzbDavArticleSegment[] decorated =
        [
            new(1, 100, " <first@test> "),
            new(2, 120, "<second@test>"),
        ];

        var first = NzbDavArticleIdentity.ComputeDirect(canonical);
        var second = NzbDavArticleIdentity.ComputeDirect(decorated);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void DirectDigest_ChangesWhenArticleOrderSizeOrNumberChanges()
    {
        NzbDavArticleSegment[] original =
        [
            new(1, 100, "first@test"),
            new(2, 120, "second@test"),
        ];

        Assert.NotEqual(
            NzbDavArticleIdentity.ComputeDirect(original),
            NzbDavArticleIdentity.ComputeDirect(original.Reverse()));
        Assert.NotEqual(
            NzbDavArticleIdentity.ComputeDirect(original),
            NzbDavArticleIdentity.ComputeDirect([new(1, 101, "first@test"), original[1]]));
        Assert.NotEqual(
            NzbDavArticleIdentity.ComputeDirect(original),
            NzbDavArticleIdentity.ComputeDirect([new(7, 100, "first@test"), original[1]]));
    }

    [Fact]
    public void ReleaseDigest_PreservesFileBoundariesAndOrder()
    {
        NzbDavArticleSegment[] first = [new(1, 100, "first@test")];
        NzbDavArticleSegment[] second = [new(1, 120, "second@test")];

        Assert.NotEqual(
            NzbDavArticleIdentity.ComputeRelease([first, second]),
            NzbDavArticleIdentity.ComputeRelease([second, first]));
        Assert.NotEqual(
            NzbDavArticleIdentity.ComputeRelease([first, second]),
            NzbDavArticleIdentity.ComputeRelease([[.. first, .. second]]));
    }

    [Fact]
    public void ArchiveMemberDigest_NormalizesInnerSeparatorsAndUsesExactSize()
    {
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(
            [[new NzbDavArticleSegment(1, 100, "archive@test")]]);

        var slash = NzbDavArticleIdentity.ComputeArchiveMember(
            releaseDigest, "Feature/Movie.mkv", 1_000);
        var backslash = NzbDavArticleIdentity.ComputeArchiveMember(
            releaseDigest, "Feature\\Movie.mkv", 1_000);
        var differentSize = NzbDavArticleIdentity.ComputeArchiveMember(
            releaseDigest, "Feature/Movie.mkv", 1_001);

        Assert.Equal(slash, backslash);
        Assert.NotEqual(slash, differentSize);
    }

    [Theory]
    [InlineData("../Movie.mkv")]
    [InlineData("/Feature/Movie.mkv")]
    [InlineData("")]
    public void ArchiveMemberDigest_RejectsUnsafeInnerPath(string path)
    {
        var releaseDigest = new string('a', 64);

        Assert.Throws<InvalidDataException>(() =>
            NzbDavArticleIdentity.ComputeArchiveMember(releaseDigest, path, 1_000));
    }
}
