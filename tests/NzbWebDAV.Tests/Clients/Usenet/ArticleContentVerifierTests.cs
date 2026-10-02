using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Models;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class ArticleContentVerifierTests : IDisposable
{
    private const int PartSize = 100;

    public ArticleContentVerifierTests() => MismatchedArticleTracker.ResetForTests();
    public void Dispose() => MismatchedArticleTracker.ResetForTests();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(100, 8)]
    [InlineData(2_743, 28)]
    [InlineData(50_000, 32)]
    public void ImportBudget_IsOnePercentClampedToEightThroughThirtyTwo(int segments, int expected)
    {
        Assert.Equal(expected, ArticleContentSampleBudget.Import.TargetFor(segments));
    }

    [Fact]
    public void SelectSamples_IncludesFirstAndLastAndSpreadsAcrossPostedFiles()
    {
        var target = new ArticleContentTarget(
            "Movie.mkv",
            [new PostedArticleFile(Ids("a", 1_000)), new PostedArticleFile(Ids("b", 1_000))]);

        var samples = ArticleContentVerifier.SelectSamples(target, ArticleContentSampleBudget.Import);

        Assert.Equal(20, samples.Count);
        Assert.Equal(new ArticleContentVerifier.SampleLocation(0, 0), samples[0]);
        Assert.Equal(new ArticleContentVerifier.SampleLocation(1, 999), samples[^1]);
        Assert.Contains(samples, sample => sample.FileIndex == 0 && sample.LocalIndex > 0);
        Assert.Contains(samples, sample => sample.FileIndex == 1 && sample.LocalIndex < 999);
        Assert.Equal(samples.Count, samples.Distinct().Count());
    }

    [Fact]
    public void SelectSamples_AddsRequestedArticlesOnTopOfTheSpread()
    {
        var ids = Ids("a", 2_743);
        var target = new ArticleContentTarget("Movie.mkv", [new PostedArticleFile(ids)]);

        var samples = ArticleContentVerifier.SelectSamples(
            target, ArticleContentSampleBudget.HealthCheck, [ids[1_234], ids[1_235]]);

        Assert.Equal(ArticleContentSampleBudget.HealthCheck.TargetFor(2_743) + 2, samples.Count);
        Assert.Contains(new ArticleContentVerifier.SampleLocation(0, 1_234), samples);
        Assert.Contains(new ArticleContentVerifier.SampleLocation(0, 1_235), samples);
    }

    [Fact]
    public async Task HealthyFile_MatchesWithBoundedBodyReads()
    {
        var file = PostedFile("ok", 2_743, out var headers);
        using var client = CreateClient(headers);

        var result = await new ArticleContentVerifier(client).VerifyAsync(
            new ArticleContentTarget("Movie.mkv", [file]),
            ArticleContentSampleBudget.Import,
            concurrency: 1,
            CancellationToken.None);

        Assert.False(result.IsDamaged);
        Assert.Equal(28, result.Sampled);
        Assert.Equal(28, client.BodyRequestCount);
        Assert.Equal(0, result.Inconclusive);
    }

    [Fact]
    public async Task ForeignTotal_IsReportedAsDifferentPost()
    {
        var file = PostedFile("sp", 2_743, out var headers);
        // The South Park collision: the article resolves to part of a 545-part upload.
        headers[file.SegmentIds[0]] = headers[file.SegmentIds[0]] with { PartNumber = 12, TotalParts = 545 };
        using var client = CreateClient(headers);

        var result = await new ArticleContentVerifier(client).VerifyAsync(
            new ArticleContentTarget("South.Park.S01E12.mkv", [file]),
            ArticleContentSampleBudget.Import,
            concurrency: 4,
            CancellationToken.None);

        Assert.True(result.HasForeignArticles);
        Assert.Equal([file.SegmentIds[0]], result.ForeignSegmentIds);
        Assert.StartsWith("1 of 28 sampled articles belong to a different post", result.Describe());
        Assert.Contains("yEnc part 12/545 for a 2743-part file", result.Describe());
    }

    [Fact]
    public async Task WrongPartNumber_IsForeignEvenWhenTotalMatches()
    {
        var file = PostedFile("pn", 10, out var headers);
        headers[file.SegmentIds[4]] = headers[file.SegmentIds[4]] with { PartNumber = 7 };
        using var client = CreateClient(headers);

        var result = await Verify(client, file);

        Assert.Equal([file.SegmentIds[4]], result.ForeignSegmentIds);
        Assert.Contains("segment 5: yEnc part 7/10", result.FirstMismatch);
    }

    [Fact]
    public async Task RangeDifferentFromKnownSegmentRange_IsForeign()
    {
        var file = PostedFile("rg", 10, out var headers) with
        {
            TrustedSegmentRanges = Enumerable.Range(0, 10)
                .Select(index => new LongRange(index * PartSize, (index + 1) * PartSize))
                .ToArray(),
        };
        headers[file.SegmentIds[3]] = headers[file.SegmentIds[3]] with { PartOffset = 350 };
        using var client = CreateClient(headers);

        var result = await Verify(client, file);

        Assert.Equal([file.SegmentIds[3]], result.ForeignSegmentIds);
        Assert.Contains("segment 4: yEnc range 351-450 instead of 301-400", result.FirstMismatch);
    }

    [Fact]
    public async Task SizeDisagreeingWithTheOtherArticles_IsForeign()
    {
        var file = PostedFile("sz", 10, out var headers);
        headers[file.SegmentIds[6]] = headers[file.SegmentIds[6]] with { FileSize = 123_456 };
        using var client = CreateClient(headers);

        var result = await Verify(client, file);

        Assert.Equal([file.SegmentIds[6]], result.ForeignSegmentIds);
        Assert.Contains("yEnc size 123456 instead of 1000", result.FirstMismatch);
    }

    [Fact]
    public async Task MissingArticle_IsDamageButNotForeign()
    {
        var file = PostedFile("ms", 10, out var headers);
        headers.Remove(file.SegmentIds[9]);
        using var client = CreateClient(headers);

        var result = await Verify(client, file);

        Assert.False(result.HasForeignArticles);
        Assert.True(result.IsDamaged);
        Assert.Equal([file.SegmentIds[9]], result.MissingSegmentIds);
    }

    [Fact]
    public async Task SiblingFallbackHoldingTheOwnArticle_KeepsTheFileHealthy()
    {
        var file = PostedFile("fb", 10, out var headers);
        var fallbacks = file.SegmentIds.Select(_ => Array.Empty<string>()).ToArray();
        fallbacks[2] = ["fb-alternate@test"];
        headers["fb-alternate@test"] = headers[file.SegmentIds[2]];
        headers[file.SegmentIds[2]] = ForeignHeader();
        using var client = CreateClient(headers);

        var result = await Verify(client, file with { SegmentFallbackIds = fallbacks });

        Assert.False(result.IsDamaged);
    }

    [Fact]
    public async Task ProviderWalk_UsesBackupThatHoldsTheOwnArticle_AndFlagsWhenNoneDoes()
    {
        var file = PostedFile("mp", 10, out var correct);
        var foreign = correct.ToDictionary(
            entry => entry.Key,
            entry => ForeignHeader(),
            StringComparer.Ordinal);
        using var wrongPost = CreateClient(foreign);
        using var correctPost = CreateClient(correct);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost, host: "correct.example", providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);

        var healthy = await Verify(client, file);
        Assert.False(healthy.IsDamaged);
        Assert.Equal(8, healthy.Sampled);
        Assert.Equal(8, correctPost.BodyRequestCount);

        MismatchedArticleTracker.ResetForTests();
        using var onlyWrong = new MultiProviderNntpClient(
            [MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example")]);
        var damaged = await Verify(onlyWrong, file);
        Assert.Equal(8, damaged.ForeignSegmentIds.Count);
    }

    [Fact]
    public void DescribeHeaderMismatch_ToleratesObfuscatedTotalsLikeStreaming()
    {
        var header = new UsenetYencHeader
        {
            FileName = "x",
            FileSize = 0,
            LineLength = 128,
            PartNumber = 3,
            TotalParts = 0,
            HasTotalParts = false,
            PartOffset = 200,
            PartSize = 100,
        };

        Assert.Null(ArticleContentVerifier.DescribeHeaderMismatch(header, 3, 10));
        Assert.NotNull(ArticleContentVerifier.DescribeHeaderMismatch(header with { PartOffset = 0 }, 3, 10));
    }

    private static Task<ArticleContentVerification> Verify(INntpClient client, PostedArticleFile file) =>
        new ArticleContentVerifier(client).VerifyAsync(
            new ArticleContentTarget("Movie.mkv", [file]),
            ArticleContentSampleBudget.Import,
            concurrency: 2,
            CancellationToken.None);

    internal static string[] Ids(string prefix, int count) =>
        Enumerable.Range(1, count).Select(index => $"{prefix}-{index}@test").ToArray();

    internal static PostedArticleFile PostedFile(
        string prefix,
        int count,
        out Dictionary<string, UsenetYencHeader> headers)
    {
        var ids = Ids(prefix, count);
        headers = new Dictionary<string, UsenetYencHeader>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
            headers[ids[index]] = HeaderFor(index + 1, count);
        return new PostedArticleFile(ids);
    }

    /// <summary>An article of a different 545-part upload (the South Park collision shape).</summary>
    internal static UsenetYencHeader ForeignHeader() => new()
    {
        FileName = "Other.Upload.mkv",
        FileSize = 545L * 716_800,
        LineLength = 128,
        PartNumber = 37,
        TotalParts = 545,
        PartOffset = 36L * 716_800,
        PartSize = 716_800,
    };

    internal static UsenetYencHeader HeaderFor(int part, int total) => new()
    {
        FileName = "movie.mkv",
        FileSize = (long)total * PartSize,
        LineLength = 128,
        PartNumber = part,
        TotalParts = total,
        PartOffset = (long)(part - 1) * PartSize,
        PartSize = PartSize,
    };

    internal static FakeNntpClient CreateClient(IReadOnlyDictionary<string, UsenetYencHeader> headers) =>
        new(
            headers.ToDictionary(entry => entry.Key, _ => new byte[PartSize], StringComparer.Ordinal),
            useCachedYencStreams: true,
            yencHeaders: headers);
}
