using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class SharedStreamsConfigTests
{
    [Fact]
    public void Defaults_AreEnabledWithDocumentedCaps()
    {
        var config = new ConfigManager();
        Assert.True(config.IsSharedStreamsEnabled());
        Assert.Equal(4, config.GetSharedStreamsMaxEntries());
        Assert.Equal(3, config.GetSharedStreamsMaxEntriesPerFile());
        Assert.Equal(32, config.GetSharedStreamsRingMb());
        Assert.Equal(10, config.GetSharedStreamsGraceSeconds());
        Assert.Equal(16, config.GetSharedStreamsSmallRangeMaxMb());
        Assert.Equal(32, config.GetSharedStreamsSmallRangeMaxMb(DavItem.ItemSubType.NzbFile));
        Assert.Equal(16, config.GetSharedStreamsSmallRangeMaxMb(DavItem.ItemSubType.MultipartFile));
    }

    [Fact]
    public void ExplicitSmallRangeLimit_OverridesBothFileTypeDefaults()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSharedStreamsSmallRangeMaxMb, ConfigValue = "8" },
        ]);
        Assert.Equal(8, config.GetSharedStreamsSmallRangeMaxMb(DavItem.ItemSubType.NzbFile));
        Assert.Equal(8, config.GetSharedStreamsSmallRangeMaxMb(DavItem.ItemSubType.MultipartFile));
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("33", 32)]
    [InlineData("abc", 4)]
    public void MaxEntries_ClampsOrDefaults(string value, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSharedStreamsMaxEntries, ConfigValue = value },
        ]);
        Assert.Equal(expected, config.GetSharedStreamsMaxEntries());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("")]
    public void Validate_AcceptsEnabledAndEmpty(string value)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSharedStreamsEnabled, ConfigValue = value },
        ]);
    }
}
