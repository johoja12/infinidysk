using System.Text.Json;
using NzbDavMigration.Canary;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryCoverageReporterTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task WriteAsync_UsesFinalDenominatorAndClassifiesEveryRemainderOnce()
    {
        var source = Directory.CreateDirectory(Path.Join(_root, "plex")).FullName;
        var library = Directory.CreateDirectory(Path.Join(_root, "plex2")).FullName;
        var target = Directory.CreateDirectory(Path.Join(_root, "target")).FullName;
        var journals = Directory.CreateDirectory(Path.Join(_root, "journals", "batch-1")).Parent!.FullName;
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var initial = new[]
        {
            Candidate("TV/exact.mkv", ids[0]),
            Candidate("TV/wrong.mkv", ids[1]),
            Candidate("TV/missing.mkv", ids[2]),
            Candidate("TV/removed.mkv", ids[3]),
        };
        var initialPath = Path.Join(_root, "initial.json");
        await File.WriteAllTextAsync(initialPath, JsonSerializer.Serialize(initial, JsonOptions));

        foreach (var item in initial.Take(3)) CreateLegacyLink(source, item.LibraryRelativePath, item.LegacyDavItemId);
        CreateLegacyLink(source, "TV/added.mkv", ids[4]);
        var master = new FullRecoveryMasterManifest(
            1, DateTimeOffset.UtcNow, 4, 4, 1m,
            initial.Select(item => new LegacySourceRecoveryItem(
                item.LibraryRelativePath, item.OriginalTarget, item.LegacyDavItemId,
                "/content/file.mkv", "exact-direct", null, "blob.nzb", new string('a', 64),
                "direct-articles-v1", new string('b', 64), 16)).ToArray());
        var masterPath = Path.Join(_root, "master.json");
        await File.WriteAllTextAsync(masterPath, JsonSerializer.Serialize(master, JsonOptions));

        var planLinks = initial.Take(3).Select((item, index) =>
        {
            var relativeTarget = $".ids/a/b/c/d/e/{Guid.NewGuid()}";
            var targetPath = Path.Join(target, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllBytes(targetPath, new byte[16]);
            return new NzbDavCanaryPlanLink(
                item.LibraryRelativePath, item.OriginalTarget, item.LegacyDavItemId, 16,
                "exact", "{}", relativeTarget, "planned");
        }).ToArray();
        var plan = new NzbDavCanaryPlan(
            1, 1, new string('c', 64), DateTimeOffset.UtcNow, 3, 3, true, planLinks);
        var planDirectory = await new NzbDavCanaryPlanWriter().WriteAsync(Path.Join(_root, "plans"), plan);
        var journalPath = Path.Join(journals, "batch-1", "apply-journal.json");
        var journal = await new CanaryLinkApplier(_ => true).ApplyAsync(
            Path.Join(planDirectory, "plan.json"), source, library, target, journalPath);

        var wrong = journal.Links.Single(item => item.LibraryRelativePath == "TV/wrong.mkv");
        File.Delete(wrong.LinkPath);
        File.CreateSymbolicLink(wrong.LinkPath, journal.Links[0].TargetPath);
        var missing = journal.Links.Single(item => item.LibraryRelativePath == "TV/missing.mkv");
        File.Delete(missing.LinkPath);

        var output = Path.Join(_root, "report");
        var result = await new CanaryCoverageReporter().WriteAsync(
            source, library, initialPath, masterPath, journals, output, 0.90m,
            TimeSpan.FromSeconds(2));

        Assert.Equal(4, result.Report.FinalSourceCount);
        Assert.Equal(1, result.Report.CoveredCount);
        Assert.Equal(1, result.Report.RemovedCount);
        Assert.Equal(1, result.Report.AddedCount);
        Assert.Equal(0.25m, result.Report.CoverageFraction);
        Assert.False(result.Report.MeetsMinimumCoverage);
        Assert.True(result.HasOwnershipErrors);
        Assert.Equal("covered", result.Report.Items.Single(item => item.LibraryRelativePath == "TV/exact.mkv").Classification);
        Assert.Equal("wrong-target", result.Report.Items.Single(item => item.LibraryRelativePath == "TV/wrong.mkv").Classification);
        Assert.Equal("missing-parallel", result.Report.Items.Single(item => item.LibraryRelativePath == "TV/missing.mkv").Classification);
        Assert.Equal("added-after-initial", result.Report.Items.Single(item => item.LibraryRelativePath == "TV/added.mkv").Classification);
        Assert.Equal(result.Report.FinalSourceCount, result.Report.Items.Count);
        Assert.True(File.Exists(Path.Join(output, "coverage.json")));
        Assert.True(File.Exists(Path.Join(output, "coverage.md")));
        Assert.True(File.Exists(Path.Join(output, "SHA256SUMS")));
        Assert.Equal(1, await NzbDavMigrationProgram.RunAsync(
        [
            "coverage-report", "--source-root", source, "--library-root", library,
            "--initial-inventory", initialPath, "--master", masterPath,
            "--journals-dir", journals, "--output", Path.Join(_root, "cli-ownership-report"),
            "--minimum-coverage", "0.90",
        ]));
    }

    [Fact]
    public async Task Command_ReturnsThreeWhenCoverageIsBelowThresholdWithoutOwnershipErrors()
    {
        var source = Directory.CreateDirectory(Path.Join(_root, "threshold-source")).FullName;
        var library = Directory.CreateDirectory(Path.Join(_root, "threshold-library")).FullName;
        var journals = Directory.CreateDirectory(Path.Join(_root, "threshold-journals")).FullName;
        var id = Guid.NewGuid();
        var initial = new[] { Candidate("TV/missing.mkv", id) };
        var initialPath = Path.Join(_root, "threshold-initial.json");
        await File.WriteAllTextAsync(initialPath, JsonSerializer.Serialize(initial, JsonOptions));
        CreateLegacyLink(source, "TV/missing.mkv", id);
        var master = new FullRecoveryMasterManifest(
            1, DateTimeOffset.UtcNow, 1, 1, 1m,
            [new LegacySourceRecoveryItem(
                initial[0].LibraryRelativePath, initial[0].OriginalTarget, id,
                "/content/file.mkv", "exact-direct", null, "blob.nzb", new string('a', 64),
                "direct-articles-v1", new string('b', 64), 16)]);
        var masterPath = Path.Join(_root, "threshold-master.json");
        await File.WriteAllTextAsync(masterPath, JsonSerializer.Serialize(master, JsonOptions));

        var exit = await NzbDavMigrationProgram.RunAsync(
        [
            "coverage-report", "--source-root", source, "--library-root", library,
            "--initial-inventory", initialPath, "--master", masterPath,
            "--journals-dir", journals, "--output", Path.Join(_root, "threshold-report"),
            "--minimum-coverage", "0.90",
        ]);

        Assert.Equal(3, exit);
    }

    private static LegacyInventoryCandidate Candidate(string path, Guid id) =>
        new(path, $"/mnt/legacy/.ids/a/b/c/d/e/{id}", id, null, "candidate", null, null);

    private static void CreateLegacyLink(string root, string relativePath, Guid id)
    {
        var path = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, $"/mnt/legacy/.ids/a/b/c/d/e/{id}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
