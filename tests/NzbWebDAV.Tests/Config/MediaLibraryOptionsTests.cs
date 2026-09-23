using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class MediaLibraryOptionsTests
{
    [Fact]
    public void ExistingInstall_DefaultsToEnabledAndFifteenMinutes()
    {
        var config = new ConfigManager();
        Assert.True(config.IsMediaLibraryEnabled());
        Assert.Equal(TimeSpan.FromMinutes(15), config.GetMediaLibraryScanInterval());
        Assert.Null(config.GetMediaLibraryPlexServerIds());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("15")]
    [InlineData("30")]
    [InlineData("60")]
    [InlineData("360")]
    public void AcceptsSupportedIntervals(string value)
    {
        ConfigManager.ValidateConfigItems([new ConfigItem
        {
            ConfigName = ConfigKeys.MediaLibraryScanIntervalMinutes,
            ConfigValue = value,
        }]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20")]
    [InlineData("abc")]
    public void RejectsUnsupportedIntervals(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([new ConfigItem
        {
            ConfigName = ConfigKeys.MediaLibraryScanIntervalMinutes,
            ConfigValue = value,
        }]));
    }

    [Fact]
    public void EmptySelectionMeansNoPlexServersWhileUnsetMeansAll()
    {
        Assert.Null(MediaLibraryOptions.ParsePlexServerIds(""));
        Assert.Empty(MediaLibraryOptions.ParsePlexServerIds("[]")!);
        Assert.Throws<ArgumentException>(() => MediaLibraryOptions.ParsePlexServerIds("[\"same\",\"same\"]"));
    }
}
