using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Services.Regrab;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchRepairHistoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("prefetch-repair-history-").FullName;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ConfigManager _config = new();
    private readonly Guid _old = Guid.NewGuid();
    private readonly DavItem _replacement = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "replacement.mkv", 100,
        DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, DateTimeOffset.UtcNow, null, null, null);

    public PrefetchRepairHistoryTests()
    {
        _connection.Open();
        _config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _root },
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/infinidysk" },
        ]);
    }

    private DavDatabaseContext Context() => new(new DbContextOptionsBuilder<DavDatabaseContext>()
        .UseSqlite(_connection).Options);
    private PrefetchJob Failed() => new("original", _old, "manual", 50, "failed", 0, 0, "old-generation", 12, null, 1)
        { FailureCode = PrefetchFailureCodes.SourceDamaged, Remedy = "repair-pending" };
    private PrefetchJob Warm(string state = "completed", string generation = "current", long start = 0,
        long length = 0, long bytes = 100) => new("replacement", _replacement.Id, "manual", 50, state,
            start, length, generation, bytes, null, 2) { FinishedAt = 2000 };

    private async Task SeedAsync(string status, bool link = true, bool item = true)
    {
        await using var ctx = Context();
        await ctx.Database.EnsureCreatedAsync();
        if (item) ctx.Items.Add(_replacement);
        ctx.ArrRegrabRequests.Add(new ArrRegrabRequest {
            Id = Guid.NewGuid(), DedupKey = $"dav:{_old}", DavItemId = _old, Source = "health-repair",
            Status = status, ReleaseName = "original-release.mkv", LibraryPath = Path.Join(_root, "episode.mkv"),
            RequestedAt = DateTime.UnixEpoch.AddSeconds(1), UpdatedAt = DateTime.UnixEpoch.AddSeconds(3),
        });
        await ctx.SaveChangesAsync();
        if (link) File.CreateSymbolicLink(Path.Join(_root, "episode.mkv"), $"/mnt/infinidysk/.ids/a/b/{_replacement.Id}");
    }

    [Fact]
    public async Task DeletedOriginal_UsesDurableTitleAndExactWarmedReplacement_WithoutChangingFailure()
    {
        await SeedAsync(ArrRegrabStatus.Replaced);
        await using var ctx = Context();
        var original = Failed();
        var result = await PrefetchRepairHistory.LoadAsync(ctx, _config, [original, Warm()],
            (_, _) => Task.FromResult<string?>("current"), CancellationToken.None);
        Assert.Equal("original-release.mkv", result[_old].DisplayName);
        Assert.Equal("replacement-warmed", result[_old].Outcome.Status);
        Assert.Equal(_replacement.Id, result[_old].Outcome.ReplacementItemId);
        Assert.Equal("failed", original.State);
        Assert.Equal(12, original.CommittedBytes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingLinkOrDeletedReplacement_DoesNotClaimRepairSuccess(bool link, bool item)
    {
        await SeedAsync(ArrRegrabStatus.Replaced, link, item);
        await using var ctx = Context();
        var result = await PrefetchRepairHistory.LoadAsync(ctx, _config, [Failed(), Warm()],
            (_, _) => throw new Exception("Must not inspect unidentified replacement"), CancellationToken.None);
        Assert.Equal("replacement-unavailable", result[_old].Outcome.Status);
        Assert.Null(result[_old].Outcome.ReplacementItemId);
    }

    [Fact]
    public async Task EarlierSuccessfulWarm_StillShowsLaterRepairOutcome()
    {
        await SeedAsync(ArrRegrabStatus.Replaced);
        await using var ctx = Context();
        var original = Failed() with { State = "completed", FailureCode = null, Remedy = null };
        var result = await PrefetchRepairHistory.LoadAsync(ctx, _config, [original, Warm()],
            (_, _) => Task.FromResult<string?>("current"), CancellationToken.None);
        Assert.Equal("replacement-warmed", result[_old].Outcome.Status);
        Assert.Equal("completed", original.State);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("requested")]
    [InlineData("search-withheld")]
    [InlineData("skipped")]
    [InlineData("failed")]
    public async Task NonReplacedRepair_PreservesCurrentOutcomeAndNeverOpensUnconfirmedReplacement(string status)
    {
        await SeedAsync(status);
        await using var ctx = Context();
        var result = await PrefetchRepairHistory.LoadAsync(ctx, _config, [Failed(), Warm()],
            (_, _) => throw new Exception("Must not inspect unconfirmed replacement"), CancellationToken.None);
        Assert.Equal(status, result[_old].Outcome.Status);
        Assert.Null(result[_old].Outcome.ReplacementItemId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignMountOrOriginalIdentity_IsNotAReplacement(bool foreignMount)
    {
        await SeedAsync(ArrRegrabStatus.Replaced);
        var path = Path.Join(_root, "episode.mkv");
        File.Delete(path);
        File.CreateSymbolicLink(path, foreignMount
            ? $"/mnt/foreign/.ids/a/b/{_replacement.Id}"
            : $"/mnt/infinidysk/.ids/a/b/{_old}");
        await using var ctx = Context();
        var result = await PrefetchRepairHistory.LoadAsync(ctx, _config, [Failed(), Warm()],
            (_, _) => throw new Exception("Must not inspect foreign or original identity"), CancellationToken.None);
        Assert.Equal("replacement-unavailable", result[_old].Outcome.Status);
        Assert.Null(result[_old].Outcome.ReplacementItemId);
    }

    [Theory]
    [InlineData("failed", "current", 0, 0, 100)]
    [InlineData("completed", "obsolete", 0, 0, 100)]
    [InlineData("completed", "current", 0, 20, 100)]
    [InlineData("completed", "current", 20, 0, 100)]
    [InlineData("completed", "current", 0, 0, 12)]
    public void PartialFailedOrObsoleteWarm_DoesNotClaimWholeFileWarm(string state, string generation,
        long start, long length, long bytes) => Assert.False(PrefetchRepairHistory.IsWholeFileWarmed(
            [Warm(state, generation, start, length, bytes)], _replacement, "current", DateTime.UnixEpoch));

    [Fact]
    public void WarmBeforeRepairRequest_DoesNotConfirmReplacement() => Assert.False(
        PrefetchRepairHistory.IsWholeFileWarmed([Warm()], _replacement, "current", DateTime.UnixEpoch.AddSeconds(3)));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
