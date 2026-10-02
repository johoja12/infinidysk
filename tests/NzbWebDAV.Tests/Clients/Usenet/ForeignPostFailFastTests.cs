using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

/// <summary>
/// #120: providers answered a release's message-ids with articles of a different post
/// (yEnc total 571 for a 3569-part file). Every retried range re-walked every provider,
/// discarding a connection per rejected body, and logged one warning per article.
/// </summary>
[Collection(nameof(GlobalLoggerCollection))]
public sealed class ForeignPostFailFastTests
{
    private const int SegmentCount = 16;
    private const int SegmentBytes = 3;

    public ForeignPostFailFastTests()
    {
        MismatchedArticleTracker.ResetForTests();
        PlaybackHoleTracker.ResetForTests();
    }

    [Fact]
    public async Task PersistentlyForeignArticles_AreAskedOncePerProviderAndFailConclusively()
    {
        var ids = CreateIds();
        var primary = CreateForeignPost(ids);
        var backup = CreateForeignPost(ids);
        using var client = CreateClient(primary, backup);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var stream = CreateStream(ids, client);
            var failure = await Assert.ThrowsAnyAsync<UsenetArticleNotFoundException>(
                () => stream.CopyToAsync(Stream.Null));
            // Every provider answered (or is known to answer) with another post: the miss is
            // proven, so the client gets a terminal error rather than a retryable 503.
            Assert.Null(failure.InconclusiveReason);
        }

        // Retried ranges skip providers already known to serve another post for the article,
        // so no article is downloaded (and its connection discarded) twice by one provider.
        Assert.All(primary.BodyRequestCounts.Values, count => Assert.Equal(1, count));
        Assert.All(backup.BodyRequestCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task FileWithManyForeignArticles_FailsFastWithoutProviderRequestsAndLogsOncePerFile()
    {
        var ids = CreateIds();
        var primary = CreateForeignPost(ids);
        var backup = CreateForeignPost(ids);
        using var client = CreateClient(primary, backup);
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            UsenetForeignPostException? verdict = null;
            // Player-style range reads at different offsets, each on a fresh stream as rclone does.
            // A mid-file read may gap-fill a lone hole (degraded tolerance) or fail after
            // consecutive holes; either way it must end, and soon end on the verdict.
            for (var segment = 0; segment < SegmentCount && verdict is null; segment += 4)
            {
                await using var stream = CreateStream(ids, client);
                stream.Seek(segment * SegmentBytes, SeekOrigin.Begin);
                try
                {
                    await stream.ReadExactlyAsync(new byte[2 * SegmentBytes]);
                }
                catch (UsenetArticleNotFoundException failure)
                {
                    verdict = failure as UsenetForeignPostException;
                }
            }

            verdict ??= await ReadVerdictAsync(ids, client);
            Assert.True(verdict.ForeignArticles >= MismatchedArticleTracker.ForeignArticlesForFileVerdict);
            Assert.Null(verdict.InconclusiveReason);

            // Once the file is proven to be another post, a read issues no provider requests.
            var requestsBefore = primary.BodyRequestCount + backup.BodyRequestCount;
            var again = await ReadVerdictAsync(ids, client, offset: (SegmentCount - 1) * SegmentBytes);
            Assert.Equal(verdict.ForeignArticles, again.ForeignArticles);
            Assert.Equal(requestsBefore, primary.BodyRequestCount + backup.BodyRequestCount);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        var events = sink.Events;
        Assert.DoesNotContain(events, e => e.Level >= LogEventLevel.Warning
            && e.MessageTemplate.Text == YencFileValidationContext.MismatchDetailTemplate);
        Assert.Contains(events, e => e.Level == LogEventLevel.Debug
            && e.MessageTemplate.Text == YencFileValidationContext.MismatchDetailTemplate);

        var fileWarning = Assert.Single(events, e => e.Level == LogEventLevel.Warning
            && e.MessageTemplate.Text.StartsWith("Rejected yEnc articles from a different post", StringComparison.Ordinal));
        Assert.Equal($"/content/show/{ids[0][..8]}.mkv", Scalar(fileWarning, "FileName"));
        Assert.Equal(571, Scalar(fileWarning, "ReturnedTotalParts"));
        Assert.Equal(SegmentCount, Scalar(fileWarning, "ExpectedTotalParts"));
        Assert.Matches("^[0-9A-F]{64}$", Assert.IsType<string>(Scalar(fileWarning, "FileRef")));

        var verdictWarning = Assert.Single(events, e => e.Level == LogEventLevel.Warning
            && e.MessageTemplate.Text.StartsWith("Stopping reads of", StringComparison.Ordinal));
        Assert.Equal($"/content/show/{ids[0][..8]}.mkv", Scalar(verdictWarning, "FileName"));
        Assert.Equal(Scalar(fileWarning, "FileRef"), Scalar(verdictWarning, "FileRef"));
        Assert.DoesNotContain("@post.example", fileWarning.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("@post.example", verdictWarning.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignArticlesOnOneProvider_DoNotFailFileServedByAnother()
    {
        var ids = CreateIds();
        var foreign = CreateForeignPost(ids);
        var genuine = new FakeNntpClient(CreateSegments(ids), useCachedYencStreams: true);
        using var client = CreateClient(foreign, genuine);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var stream = CreateStream(ids, client);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(CreateSegments(ids).SelectMany(pair => pair.Value), output.ToArray());
        }

        // The foreign provider is asked once per article; later reads go straight to the
        // provider that holds the real post.
        Assert.All(foreign.BodyRequestCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(3 * SegmentCount, genuine.BodyRequestCount);
    }

    [Fact]
    public async Task SlowButProgressingStream_IsNotCut()
    {
        var ids = CreateIds();
        var slow = new DelayingNntpClient(
            new FakeNntpClient(CreateSegments(ids), useCachedYencStreams: true),
            TimeSpan.FromMilliseconds(40));
        using var client = new MultiProviderNntpClient(
            [MultiProviderNntpClientTests.CreateProvider(slow, host: "slow.example")]);

        await using var stream = CreateStream(ids, client);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        Assert.Equal(CreateSegments(ids).SelectMany(pair => pair.Value), output.ToArray());
        Assert.Equal(SegmentCount, slow.Requests);
    }

    private static async Task<UsenetForeignPostException> ReadVerdictAsync(
        string[] ids, INntpClient client, long offset = 0)
    {
        await using var stream = CreateStream(ids, client);
        stream.Seek(offset, SeekOrigin.Begin);
        return await Assert.ThrowsAsync<UsenetForeignPostException>(
            () => stream.ReadAsync(new byte[1]).AsTask());
    }

    private static string[] CreateIds()
    {
        var prefix = Guid.NewGuid().ToString("N");
        return Enumerable.Range(0, SegmentCount).Select(index => $"{prefix}-{index}@post.example").ToArray();
    }

    private static Dictionary<string, byte[]> CreateSegments(string[] ids) =>
        ids.Select((id, index) => (id, Bytes: Enumerable.Repeat((byte)(index + 1), SegmentBytes).ToArray()))
            .ToDictionary(pair => pair.id, pair => pair.Bytes, StringComparer.Ordinal);

    // Shape of the production rejections: the article for position N of a 3569-part file
    // came back as part 367/571 of a ~194 MB post with inconsistent part geometry.
    private static FakeNntpClient CreateForeignPost(string[] ids) =>
        new(
            CreateSegments(ids),
            useCachedYencStreams: true,
            yencHeaders: ids.Select((id, index) => (id, Header: new UsenetYencHeader
            {
                FileName = "other-post.bin",
                FileSize = 194_482_176,
                LineLength = 128,
                PartNumber = 367 + index,
                TotalParts = 571,
                PartOffset = 123_765_760,
                PartSize = 258_048,
            })).ToDictionary(pair => pair.id, pair => pair.Header, StringComparer.Ordinal));

    private static MultiProviderNntpClient CreateClient(INntpClient primary, INntpClient backup) =>
        new(
            [
                MultiProviderNntpClientTests.CreateProvider(primary, host: "primary.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    backup, host: "backup.example", providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);

    private static NzbFileStream CreateStream(string[] ids, INntpClient client) =>
        new(
            ids,
            fileSize: ids.Length * SegmentBytes,
            client,
            articleBufferSize: 0,
            segmentByteRanges: Enumerable.Range(0, ids.Length)
                .Select(index => LongRange.FromStartAndSize(index * SegmentBytes, SegmentBytes))
                .ToArray(),
            usePipelinedBodyRequests: false,
            fileName: $"/content/show/{ids[0][..8]}.mkv");

    private static object? Scalar(LogEvent logEvent, string property) =>
        Assert.IsType<ScalarValue>(logEvent.Properties[property]).Value;

    private sealed class DelayingNntpClient(INntpClient inner, TimeSpan delay) : WrappingNntpClient(inner)
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(delay, cancellationToken);
            return await base.DecodedBodyAsync(segmentId, cancellationToken);
        }

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(delay, cancellationToken);
            return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
        }
    }
}
