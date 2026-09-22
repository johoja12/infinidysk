using NzbDavMigration.Export;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavSourceNameResolverTests
{
    [Fact]
    public void FromHistory_PreservesAuthoritativeNames()
    {
        var names = NzbDavSourceNameResolver.FromHistory(
            "Outlander.S02E01.nzb",
            "Outlander.S02E01");

        Assert.Equal(new NzbDavSourceNames("Outlander.S02E01.nzb", "Outlander.S02E01"), names);
    }

    [Theory]
    [InlineData(null, "Outlander.S02E01")]
    [InlineData("Outlander.S02E01.nzb", null)]
    [InlineData("Outlander.S02E01.nzb", "Different.Release")]
    public void FromHistory_RejectsMissingOrInconsistentNames(string? fileName, string? jobName)
    {
        Assert.Throws<InvalidDataException>(() => NzbDavSourceNameResolver.FromHistory(fileName, jobName));
    }

    [Fact]
    public void FromLegacyPaths_DerivesCommonReleaseFolder()
    {
        var names = NzbDavSourceNameResolver.FromLegacyPaths(
        [
            "/content/sonarr/Recovered.Show.S01E01/episode.mkv",
            "/content/sonarr/Recovered.Show.S01E01/subtitle.srt",
        ]);

        Assert.Equal(
            new NzbDavSourceNames("Recovered.Show.S01E01.nzb", "Recovered.Show.S01E01"),
            names);
    }

    [Theory]
    [InlineData("/content/sonarr/Recovered.Show.S01E01/episode.mkv", "/content/sonarr/Other.Show/episode.mkv")]
    [InlineData("/view/sonarr/Recovered.Show.S01E01/episode.mkv", "/view/sonarr/Recovered.Show.S01E01/subtitle.srt")]
    public void FromLegacyPaths_RejectsAmbiguousOrUnexpectedPaths(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => NzbDavSourceNameResolver.FromLegacyPaths([first, second]));
    }
}
