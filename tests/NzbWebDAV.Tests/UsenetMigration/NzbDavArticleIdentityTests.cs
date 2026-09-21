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

    [Fact]
    public void StableArchiveDigest_IgnoresPublishedNameAndUsesExactRanges()
    {
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(
            [[new NzbDavArticleSegment(1, 100, "archive@test")]]);
        var parts = new[]
        {
            new NzbDavArchivePartIdentity(
                [new NzbDavArticleSegment(1, 100, "archive@test")],
                SegmentStart: 0,
                SegmentLength: 100,
                FileStart: 4_096,
                FileLength: 80),
        };

        var first = NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, 80);
        var renamed = NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, 80);
        var movedRange = NzbDavStableArchiveIdentity.Compute(
            releaseDigest,
            [parts[0] with { FileStart = 8_192 }],
            80);

        Assert.Equal(first, renamed);
        Assert.NotEqual(first, movedRange);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void StableArchiveDigest_RejectsMissingPartsAndInvalidRanges()
    {
        var releaseDigest = new string('a', 64);

        Assert.Throws<InvalidDataException>(() =>
            NzbDavStableArchiveIdentity.Compute(releaseDigest, [], 1));
        Assert.Throws<InvalidDataException>(() =>
            NzbDavStableArchiveIdentity.Compute(releaseDigest,
                [new NzbDavArchivePartIdentity(
                    [new NzbDavArticleSegment(1, 1, "one@test")],
                    SegmentStart: -1,
                    SegmentLength: 1,
                    FileStart: 0,
                    FileLength: 1)],
                1));
    }
}
