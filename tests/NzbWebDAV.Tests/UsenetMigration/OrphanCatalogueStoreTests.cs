using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbDavMigration.Catalogue;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class OrphanCatalogueStoreTests : IAsyncDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"orphan-catalogue-{Guid.NewGuid():N}");

    [Fact]
    public async Task NewStore_UpsertsAndResumesTheSameFrozenInput()
    {
        var (database, summary) = Paths();
        await using (var store = new OrphanCatalogueStore(database, summary, batchSize: 2))
        {
            await store.BeginAsync(Digest('a'));
            await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "one@test"));
            await store.UpsertAsync(Blob("two.nzb", Digest('2'), "release-b", "two@test"));
        }

        await using var resumed = new OrphanCatalogueStore(database, summary, batchSize: 2);
        await resumed.BeginAsync(Digest('a'));
        var state = await resumed.ReadStateAsync();
        Assert.Equal("building", state.Status);
        Assert.Equal(2, state.BlobCount);
        Assert.Equal(2, state.ArticleCount);
    }

    [Fact]
    public async Task Upsert_ReplacesBlobArticlesAndGroupsDuplicatePayloads()
    {
        var (database, summary) = Paths();
        await using var store = new OrphanCatalogueStore(database, summary);
        await store.BeginAsync(Digest('a'));
        await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "old@test"));
        await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "new@test"));
        await store.UpsertAsync(Blob("copy.nzb", Digest('1'), "release-a", "new@test"));

        Assert.Equal(["copy.nzb", "one.nzb"], await store.FindBlobPathsBySha256Async(Digest('1')));
        var state = await store.ReadStateAsync();
        Assert.Equal(2, state.BlobCount);
        Assert.Equal(2, state.ArticleCount);
    }

    [Fact]
    public async Task Upsert_PreservesDistinctArticleValuesAcrossPreparedInserts()
    {
        var (database, summary) = Paths();
        await using var store = new OrphanCatalogueStore(database, summary);
        await store.BeginAsync(Digest('a'));
        OrphanCatalogueArticle[] articles =
        [
            new("first@test", 0, 1, 123),
            new("second@test", 0, 2, 456),
            new("third@test", 1, 1, 789),
        ];
        await store.UpsertAsync(new OrphanCatalogueBlob("multi.nzb", 100, 200,
            Digest('1'), "valid", null, Digest('3'), articles));

        var reloaded = await store.ReadBlobAsync("multi.nzb");
        Assert.Equal(articles, reloaded!.Articles);
    }

    [Fact]
    public async Task FindBlobsContainingAll_VerifiesEveryIdAfterBoundedProbe()
    {
        var (database, summary) = Paths();
        await using var store = new OrphanCatalogueStore(database, summary);
        await store.BeginAsync(Digest('a'));
        var ids = Enumerable.Range(0, 1000).Select(index => $"id-{index}@test").ToArray();
        const int probeCount = 128;
        var sampled = Enumerable.Range(0, probeCount)
            .Select(index => (int)((long)index * (ids.Length - 1) / (probeCount - 1)))
            .ToHashSet();
        var absent = Enumerable.Range(1, ids.Length - 2).First(index => !sampled.Contains(index));
        await store.UpsertAsync(new OrphanCatalogueBlob("complete.nzb", 100, 200,
            Digest('1'), "valid", null, Digest('3'),
            ids.Select((id, index) => new OrphanCatalogueArticle(id, 0, index, 100)).ToArray()));
        await store.UpsertAsync(new OrphanCatalogueBlob("decoy.nzb", 100, 200,
            Digest('2'), "valid", null, Digest('4'),
            ids.Where((_, index) => index != absent)
                .Select((id, index) => new OrphanCatalogueArticle(id, 0, index, 100)).ToArray()));

        var found = await store.FindBlobsContainingAllAsync(ids);

        Assert.Equal("complete.nzb", Assert.Single(found).RelativePath);
    }

    [Fact]
    public async Task CancelledBuild_PersistsThePartialBatchForResume()
    {
        var (database, summary) = Paths();
        await using (var store = new OrphanCatalogueStore(database, summary, batchSize: 100))
        {
            await store.BeginAsync(Digest('a'));
            await store.UpsertAsync(Blob("partial.nzb", Digest('1'), "release-a", "one@test"));
        }

        await using var resumed = new OrphanCatalogueStore(database, summary);
        await resumed.BeginAsync(Digest('a'));
        var state = await resumed.ReadStateAsync();
        Assert.Equal("building", state.Status);
        Assert.Equal(1, state.BlobCount);
    }

    [Fact]
    public async Task FailedBlobUpsert_RollsBackThatBlobButKeepsEarlierCompletedWork()
    {
        var (database, summary) = Paths();
        await using (var store = new OrphanCatalogueStore(database, summary, batchSize: 100))
        {
            await store.BeginAsync(Digest('a'));
            await store.UpsertAsync(Blob("complete.nzb", Digest('1'), "release-a", "one@test"));
            var duplicate = new OrphanCatalogueArticle("duplicate@test", 0, 0, 123);
            var invalid = new OrphanCatalogueBlob("partial.nzb", 123, 456, Digest('2'), "valid", null,
                "release-b", [duplicate, duplicate]);
            await Assert.ThrowsAsync<SqliteException>(() => store.UpsertAsync(invalid));
        }

        await using var resumed = new OrphanCatalogueStore(database, summary);
        await resumed.BeginAsync(Digest('a'));
        var state = await resumed.ReadStateAsync();
        Assert.Equal(1, state.BlobCount);
        Assert.Equal(1, state.ArticleCount);
    }

    [Fact]
    public async Task Begin_RejectsAChangedFrozenInputDigest()
    {
        var (database, summary) = Paths();
        await using var store = new OrphanCatalogueStore(database, summary);
        await store.BeginAsync(Digest('a'));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginAsync(Digest('b')));
    }

    [Fact]
    public async Task Seal_CompletesStateWritesChecksummedSummaryAndCannotResumeWithDifferentInput()
    {
        var (database, summaryPath) = Paths();
        var summary = new OrphanCatalogueSummary(1, 1, 0, 0, Digest('a'));
        await using (var store = new OrphanCatalogueStore(database, summaryPath))
        {
            await store.BeginAsync(Digest('a'));
            await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "one@test"));
            await store.SealAsync(summary);
            var state = await store.ReadStateAsync();
            Assert.Equal("complete", state.Status);
            Assert.NotNull(state.CompletedAt);
        }

        var document = JsonSerializer.Deserialize<OrphanCatalogueCompletionDocument>(
            await File.ReadAllTextAsync(summaryPath))!;
        Assert.Equal(summary, document.Summary);
        Assert.Equal(64, document.Sha256.Length);
        Assert.Equal(OrphanCatalogueCompletionDocument.ComputeDigest(summary), document.Sha256);

        await using var reopened = new OrphanCatalogueStore(database, summaryPath);
        await reopened.BeginAsync(Digest('a'));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.BeginAsync(Digest('b')));
        await using var observer = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        await observer.OpenAsync();
        await using var journalMode = observer.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode";
        Assert.Equal("delete", await journalMode.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OpenCompleted_AddsBlobArticleLookupIndexToOlderCatalogue()
    {
        var (database, summaryPath) = Paths();
        await using (var store = new OrphanCatalogueStore(database, summaryPath))
        {
            await store.BeginAsync(Digest('a'));
            await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "one@test"));
            await store.SealAsync(new OrphanCatalogueSummary(1, 1, 0, 1, Digest('a')));
        }

        await using (var legacy = new SqliteConnection($"Data Source={database}"))
        {
            await legacy.OpenAsync();
            await using var drop = legacy.CreateCommand();
            drop.CommandText = "DROP INDEX IX_Articles_BlobPath_Order";
            await drop.ExecuteNonQueryAsync();
        }

        await using var reopened = new OrphanCatalogueStore(database, summaryPath);
        await reopened.OpenCompletedAsync();
        Assert.Equal("one@test", Assert.Single((await reopened.ReadBlobAsync("one.nzb"))!.Articles).MessageId);

        await using var observer = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        await observer.OpenAsync();
        await using var plan = observer.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN SELECT MessageId,FileOrdinal,SegmentOrdinal,SegmentBytes
            FROM Articles WHERE BlobPath=$path ORDER BY FileOrdinal,SegmentOrdinal,MessageId
            """;
        plan.Parameters.AddWithValue("$path", "one.nzb");
        await using var reader = await plan.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Contains("IX_Articles_BlobPath_Order", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseAndSummary_ArePrivateOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var (database, summaryPath) = Paths();
        await using var store = new OrphanCatalogueStore(database, summaryPath);
        await store.BeginAsync(Digest('a'));
        await store.SealAsync(new OrphanCatalogueSummary(0, 0, 0, 0, Digest('a')));

        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(database));
        Assert.Equal(expected, File.GetUnixFileMode(summaryPath));
    }

    [Fact]
    public async Task Upsert_CommitsInBoundedBatches()
    {
        var (database, summary) = Paths();
        await using var store = new OrphanCatalogueStore(database, summary, batchSize: 2);
        await store.BeginAsync(Digest('a'));
        await store.UpsertAsync(Blob("one.nzb", Digest('1'), "release-a", "one@test"));
        await store.UpsertAsync(Blob("two.nzb", Digest('2'), "release-b", "two@test"));

        await using var observer = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        await observer.OpenAsync();
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Blobs";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    private (string Database, string Summary) Paths()
    {
        Directory.CreateDirectory(_root);
        return (Path.Join(_root, "catalogue.sqlite"), Path.Join(_root, "catalogue-summary.json"));
    }

    private static OrphanCatalogueBlob Blob(string path, string sha256, string releaseDigest, string messageId) =>
        new(path, 123, 456, sha256, "valid", null, releaseDigest,
            [new OrphanCatalogueArticle(messageId, 0, 0, 123)]);

    private static string Digest(char value) => new(value, 64);

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        await ValueTask.CompletedTask;
    }
}
