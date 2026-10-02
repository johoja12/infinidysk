using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Clients.Usenet;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public sealed class ImportArticleContentValidatorTests : IDisposable
{
    public ImportArticleContentValidatorTests() => MismatchedArticleTracker.ResetForTests();
    public void Dispose() => MismatchedArticleTracker.ResetForTests();

    [Fact]
    public void PlanTargets_VerifiesOnlyImportedMedia_NotPar2SamplesOrNfos()
    {
        var results = new List<BaseProcessor.Result>
        {
            DirectFile("Movie.mkv", 2_000_000_000, 40),
            DirectFile("Movie.sample.mkv", 50_000_000, 2),
            DirectFile("Movie.nfo", 2_000, 1),
            DirectFile("Movie.par2", 10_000, 1),
            DirectFile("Movie.vol00+01.par2", 700_000, 2),
        };

        var targets = ImportArticleContentValidator.PlanTargets(results, "movies", "Job", new ConfigManager());

        var target = Assert.Single(targets);
        Assert.Equal("Movie.mkv", target.Name);
        Assert.Equal(40, target.SegmentCount);
    }

    [Fact]
    public void PlanTargets_ArchivedMovie_VerifiesEveryVolumeAsItsOwnPostedFile()
    {
        var volumes = Enumerable.Range(0, 3)
            .Select(index => NzbFileWith($"vol{index}", $"archive.part{index + 1}.rar", 5))
            .ToList();
        var archived = new RarProcessor.Result
        {
            StoredFileSegments = volumes.Select((volume, index) => new RarProcessor.StoredFileSegment
            {
                NzbFile = volume,
                PartSize = 1000,
                ArchiveName = "archive.rar",
                PartNumber = new RarProcessor.PartNumber { PartNumberFromHeader = index },
                ReleaseDate = DateTimeOffset.UnixEpoch,
                PathWithinArchive = "Archived.Movie.mkv",
                ByteRangeWithinPart = new LongRange(0, 1000),
                AesParams = null,
                FileUncompressedSize = 3000,
            }).ToArray(),
        };

        var target = Assert.Single(ImportArticleContentValidator.PlanTargets(
            [archived], "movies", "Job", new ConfigManager()));

        Assert.Equal(3, target.Files.Count);
        Assert.All(target.Files, file => Assert.Equal(5, file.SegmentIds.Length));
    }

    [Fact]
    public async Task ValidateAsync_ForeignSampledArticle_FailsWithReleaseDamagedMessage()
    {
        var file = ArticleContentVerifierTests.PostedFile("sp", 2_743, out var headers);
        headers[file.SegmentIds[^1]] = ArticleContentVerifierTests.ForeignHeader();
        using var client = ArticleContentVerifierTests.CreateClient(headers);

        var error = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            new ImportArticleContentValidator(client).ValidateAsync(
                [new ArticleContentTarget("South.Park.S01E12.mkv", [file])],
                ArticleContentSampleBudget.Import,
                concurrency: 4,
                "South.Park.S01E12",
                CancellationToken.None));

        Assert.StartsWith(
            "Release damaged on Usenet: 1 of 28 sampled articles belong to a different post (South.Park.S01E12.mkv",
            error.Message);
    }

    [Fact]
    public async Task ValidateAsync_MissingSampledArticle_DoesNotFailTheImport()
    {
        var file = ArticleContentVerifierTests.PostedFile("ms", 50, out var headers);
        headers.Remove(file.SegmentIds[^1]);
        using var client = ArticleContentVerifierTests.CreateClient(headers);

        await new ImportArticleContentValidator(client).ValidateAsync(
            [new ArticleContentTarget("Movie.mkv", [file])],
            ArticleContentSampleBudget.Import,
            concurrency: 4,
            "Movie",
            CancellationToken.None);
    }

    private static FileProcessor.Result DirectFile(string fileName, long fileSize, int segments) => new()
    {
        NzbFile = NzbFileWith(Path.GetFileNameWithoutExtension(fileName), fileName, segments),
        FileName = fileName,
        FileSize = fileSize,
        ReleaseDate = DateTimeOffset.UnixEpoch,
    };

    private static NzbFile NzbFileWith(string prefix, string fileName, int segments)
    {
        var file = new NzbFile { Subject = $"\"{fileName}\" yEnc (1/{segments})" };
        for (var index = 1; index <= segments; index++)
            file.Segments.Add(new NzbSegment { MessageId = $"{prefix}-{index}@test", Bytes = 1000, Number = index });
        return file;
    }
}

/// <summary>
/// End-to-end SAB queue import: a release whose sampled article resolves to another post
/// fails into history with the operator-facing reason, a healthy release imports with a
/// bounded number of extra reads, and non-imported extras (an NFO) are never sampled.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class ImportArticleContentQueueTests : IAsyncLifetime
{
    private const int SegmentCount = 20;
    private const int PartSize = 1000;
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-content-verify-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        MismatchedArticleTracker.ResetForTests();
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Join(_configRoot, "db.sqlite")}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        MismatchedArticleTracker.ResetForTests();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_configRoot))
                Directory.Delete(_configRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task ForeignSampledArticle_FailsImportIntoHistoryWithClearMessage()
    {
        // Segment 9 is inside the deterministic 8-article spread for a 20-segment file.
        using var client = new ReleaseClient(foreignSegment: 9);

        var history = await ImportAsync(client);

        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, history.DownloadStatus);
        Assert.StartsWith(
            "Release damaged on Usenet: 1 of 8 sampled articles belong to a different post (Movie.mkv",
            history.FailMessage);
        Assert.Empty(await _context.Items.AsNoTracking().Where(x => x.Name.EndsWith(".mkv")).ToListAsync());
    }

    [Fact]
    public async Task HealthyRelease_ImportsWithBoundedExtraReads_AndNeverSamplesExtras()
    {
        using var client = new ReleaseClient(foreignSegment: null);

        var history = await ImportAsync(client);

        Assert.True(
            history.DownloadStatus == HistoryItem.DownloadStatusOption.Completed,
            $"Expected Completed; got {history.DownloadStatus}: {history.FailMessage}");
        Assert.Single(await _context.Items.AsNoTracking().Where(x => x.Name.EndsWith(".mkv")).ToListAsync());
        var contentReads = client.BodyRequests
            .Where(entry => entry.Key.Stage == "content-verification")
            .ToList();
        Assert.Equal(8, contentReads.Sum(entry => entry.Value));
        Assert.DoesNotContain(contentReads, entry => entry.Key.SegmentId.StartsWith("nfo-", StringComparison.Ordinal));
    }

    private async Task<HistoryItem> ImportAsync(ReleaseClient client)
    {
        var nzbBytes = Encoding.UTF8.GetBytes(NzbXml);
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Movie.nzb",
            JobName = "Movie.Release",
            NzbFileSize = nzbBytes.Length,
            TotalSegmentBytes = SegmentCount * PartSize,
            Category = "migration-plex",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        _context.QueueItems.Add(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        queueItem = await _context.QueueItems.SingleAsync(x => x.Id == queueItem.Id);

        var config = new ConfigManager();
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
        var processor = new QueueItemProcessor(
            queueItem,
            new MemoryStream(nzbBytes),
            _dbClient,
            client,
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new Progress<int>(),
            new ConcurrentDictionary<Guid, int>(),
            finalizeLock: null,
            healthCheckConnectionGate,
            CancellationToken.None,
            stageReporter: stage => client.Stage = stage);

        await processor.ProcessAsync().WaitAsync(TimeSpan.FromSeconds(30));

        _context.ChangeTracker.Clear();
        return Assert.Single(await _context.HistoryItems.AsNoTracking().ToListAsync());
    }

    private static readonly string NzbXml =
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;Movie.mkv&quot; yEnc (1/{SegmentCount})">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
        {string.Join("\n", Enumerable.Range(1, SegmentCount).Select(index =>
            $"      <segment bytes=\"{PartSize}\" number=\"{index}\">mkv-{index}@test</segment>"))}
            </segments>
          </file>
          <file subject="&quot;Movie.nfo&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="100" number="1">nfo-1@test</segment>
            </segments>
          </file>
        </nzb>
        """;

    /// <summary>Serves a 20-article Movie.mkv and a one-article NFO, optionally with one foreign article.</summary>
    private sealed class ReleaseClient(int? foreignSegment) : NntpClient
    {
        public ConcurrentDictionary<(string Stage, string SegmentId), int> BodyRequests { get; } = new();

        private volatile string _stage = "start";

        public string Stage
        {
            get => _stage;
            set => _stage = value;
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            var key = segmentId.ToString();
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article",
                Stream = CreateStream(key, ownPost: true),
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                    },
                },
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            BodyRequests.AddOrUpdate((Stage, key), 1, (_, count) => count + 1);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = CreateStream(key, ownPost: key != $"mkv-{foreignSegment}@test"),
            });
        }

        private static CachedYencStream CreateStream(string key, bool ownPost)
        {
            if (key.StartsWith("nfo-", StringComparison.Ordinal))
            {
                return new CachedYencStream(
                    new UsenetYencHeader
                    {
                        FileName = "Movie.nfo",
                        FileSize = 100,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 1,
                        PartOffset = 0,
                        PartSize = 100,
                    },
                    new MemoryStream(new byte[100], writable: false));
            }

            var part = int.Parse(key["mkv-".Length..key.IndexOf('@', StringComparison.Ordinal)]);
            var payload = new byte[PartSize];
            if (part == 1) new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(payload, 0);
            var header = new UsenetYencHeader
            {
                FileName = "Movie.mkv",
                FileSize = SegmentCount * PartSize,
                LineLength = 128,
                PartNumber = part,
                TotalParts = SegmentCount,
                PartOffset = (long)(part - 1) * PartSize,
                PartSize = PartSize,
            };
            if (!ownPost)
                header = header with { FileName = "Other.Upload.mkv", PartNumber = 3, TotalParts = 545 };
            return new CachedYencStream(header, new MemoryStream(payload, writable: false));
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
