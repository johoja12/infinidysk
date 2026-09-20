using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchSettingsTests
{
    [Fact]
    public void ConfidenceAndCooldown_AreExplicitConfigurableControls()
    {
        var settings = PrefetchSettings.Parse("{\"ConfidenceThreshold\":0.8,\"CooldownMinutes\":3}");
        Assert.Equal(0.8, settings.ConfidenceThreshold);
        Assert.Equal(3, settings.CooldownMinutes);
        var limits = PrefetchSettings.Parse("{\"QueueCapacity\":8,\"MaxRetries\":0,\"IntentTtlHours\":2,\"VerifiedSessionExpirySeconds\":45}");
        Assert.Equal(8, limits.QueueCapacity);
        Assert.Equal(0, limits.MaxRetries);
        Assert.Equal(2, limits.IntentTtlHours);
        Assert.Equal(45, limits.VerifiedSessionExpirySeconds);
    }

    [Fact]
    public void DefaultPolicies_AreOffAndBounded()
    {
        var settings = PrefetchSettings.Parse(null);
        Assert.False(settings.Enabled);
        Assert.Equal(10_000_000_000, settings.DailyByteBudget);
        Assert.True(settings.FullFileWarming);
        Assert.InRange(settings.MaxConcurrentJobs, 1, 4);
    }

    [Theory]
    [InlineData("{\"MaxConcurrentJobs\":100}")]
    [InlineData("{\"RealtimeCheckIntervalSeconds\":0}")]
    [InlineData("{\"Sources\":[{\"ServerId\":\"one\",\"Key\":\"https://other-host/steal\"}]}")]
    [InlineData("{\"Sources\":null}")]
    [InlineData("{\"Users\":null}")]
    [InlineData("{\"ConfidenceThreshold\":1.1}")]
    [InlineData("{\"CooldownMinutes\":0}")]
    public void UnsafeOrUnboundedPolicies_AreRejected(string json) =>
        Assert.Throws<ArgumentException>(() => PrefetchSettings.Parse(json));
}
