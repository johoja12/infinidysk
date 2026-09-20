using NzbDavMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryValidatorTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"canary-validator-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidateAsync_PerformsBoundedBeginningMiddleAndEndReads()
    {
        var target = Path.Join(_root, "target.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(target, new byte[1024 * 1024]);
        var link = Path.Join(_root, "link.bin");
        File.CreateSymbolicLink(link, target);
        var journal = new CanaryApplyJournal
        {
            PlanPath = Path.Join(_root, "plan.json"),
            PlanSha256 = new string('a', 64),
            LibraryRoot = _root,
            TargetRoot = _root,
            Links = [new CanaryApplyJournalLink
            {
                LibraryRelativePath = "link.bin",
                LinkPath = link,
                TargetPath = target,
                ExpectedFileSize = 1024 * 1024,
                Status = "applied",
            }],
        };
        var journalPath = Path.Join(_root, "journal.json");
        await CanaryJournalStore.WriteAsync(journalPath, journal);

        var results = await new CanaryValidator().ValidateAsync(
            journalPath, maximumBytesPerRead: 64 * 1024, timeout: TimeSpan.FromSeconds(2));

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal(["beginning", "middle", "end"], result.Reads.Select(read => read.Position));
        Assert.All(result.Reads, read => Assert.InRange(read.BytesRead, 1, 64 * 1024));
    }

    [Fact]
    public async Task ValidateAsync_ReportsRetargetedLinkWithoutFollowingIt()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Join(_root, "original.bin");
        var replacement = Path.Join(_root, "replacement.bin");
        await File.WriteAllBytesAsync(original, new byte[4]);
        await File.WriteAllBytesAsync(replacement, new byte[4]);
        var link = Path.Join(_root, "link.bin");
        File.CreateSymbolicLink(link, replacement);
        var journalPath = Path.Join(_root, "journal.json");
        await CanaryJournalStore.WriteAsync(journalPath, new CanaryApplyJournal
        {
            PlanPath = "plan",
            PlanSha256 = new string('b', 64),
            LibraryRoot = _root,
            TargetRoot = _root,
            Links = [new CanaryApplyJournalLink
            {
                LibraryRelativePath = "link.bin", LinkPath = link, TargetPath = original,
                ExpectedFileSize = 4, Status = "applied",
            }],
        });

        var result = Assert.Single(await new CanaryValidator().ValidateAsync(journalPath));

        Assert.False(result.Success);
        Assert.Contains("target", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
