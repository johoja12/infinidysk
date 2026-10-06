using NzbDavMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryValidatorTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"canary-validator-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ValidateAsync_PerformsBoundedBeginningMiddleAndEndReads(int workers)
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
            journalPath, maximumBytesPerRead: 64 * 1024, timeout: TimeSpan.FromSeconds(2), workers: workers);

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

    [Fact]
    public async Task ValidateAsync_BoundsConcurrencyAndPreservesJournalOrder()
    {
        var journalPath = await WriteParallelJournalAsync();
        var firstWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var calls = 0;
        var validator = new CanaryValidator(async (link, maxRead, timeout, ffprobe, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
            } while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            if (Interlocked.Increment(ref calls) == 4)
                firstWorkersStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return new CanaryValidationResult(link.LibraryRelativePath, true,
                    link.ExpectedFileSize, link.ExpectedFileSize, [], null, null);
            }
            finally { Interlocked.Decrement(ref active); }
        });

        var validation = validator.ValidateAsync(journalPath, workers: 4);
        await firstWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref active));
        Assert.Equal(4, Volatile.Read(ref calls));
        release.SetResult();
        var results = await validation;

        Assert.Equal(4, peak);
        Assert.Equal(8, calls);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"link-{i}.bin"),
            results.Select(result => result.LibraryRelativePath));
    }

    [Fact]
    public async Task ValidateAsync_CancelsAllActiveWorkers()
    {
        var journalPath = await WriteParallelJournalAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var validator = new CanaryValidator(async (link, maxRead, timeout, ffprobe, token) =>
        {
            if (Interlocked.Increment(ref active) == 4) started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable");
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var cancellation = new CancellationTokenSource();
        var validation = validator.ValidateAsync(journalPath, cancellationToken: cancellation.Token, workers: 4);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task ValidateAsync_RejectsUnsafeWorkerCounts(int workers)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CanaryValidator().ValidateAsync("unused", workers: workers));
    }

    private async Task<string> WriteParallelJournalAsync()
    {
        Directory.CreateDirectory(_root);
        var journalPath = Path.Join(_root, "parallel-journal.json");
        await CanaryJournalStore.WriteAsync(journalPath, new CanaryApplyJournal
        {
            PlanPath = "plan", PlanSha256 = new string('c', 64),
            LibraryRoot = _root, TargetRoot = _root,
            Links = Enumerable.Range(0, 8).Select(i => new CanaryApplyJournalLink
            {
                LibraryRelativePath = $"link-{i}.bin", LinkPath = Path.Join(_root, $"link-{i}.bin"),
                TargetPath = Path.Join(_root, $"target-{i}.bin"), ExpectedFileSize = 4, Status = "applied",
            }).Append(new CanaryApplyJournalLink
            {
                LibraryRelativePath = "excluded.bin", LinkPath = Path.Join(_root, "excluded.bin"),
                TargetPath = Path.Join(_root, "excluded-target.bin"), ExpectedFileSize = 4,
                Status = "source-replaced",
            }).ToList(),
        });
        return journalPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
