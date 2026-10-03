using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class WarmConnectionGovernorTests
{
    [Fact]
    public void Target_UsesFreeCapacityAboveThePlaybackReserve()
    {
        // 40 transfer slots, 10 busy (4 of them ours): 30 free, reserve 10 → 4 + 30 - 10.
        Assert.Equal(24, WarmConnectionGovernor.Target(2, 4, [Provider(limit: 40, active: 10)]));
        // 40 slots, 30 busy (4 ours): 10 free, reserve 10 → keeps what it holds.
        Assert.Equal(4, WarmConnectionGovernor.Target(2, 4, [Provider(limit: 40, active: 30)]));
    }

    [Fact]
    public void Target_FallsToTheFloorWhenAnyProviderHasWaitingTransfers()
    {
        Assert.Equal(2, WarmConnectionGovernor.Target(2, 12,
            [Provider(limit: 90, active: 12), Provider(limit: 40, active: 39, waiting: 1)]));
    }

    [Fact]
    public void Target_StaysWithinFloorAndCeiling()
    {
        Assert.Equal(3, WarmConnectionGovernor.Target(3, 0, [Provider(limit: 8, active: 8)]));
        Assert.Equal(3, WarmConnectionGovernor.Target(3, 0, []));
        Assert.Equal(WarmConnectionGovernor.Ceiling, WarmConnectionGovernor.Target(2, 0, [Provider(limit: 400, active: 0)]));
    }

    [Fact]
    public void Target_CountsUnadmittedProvidersByAvailableConnections()
    {
        // 60 connections, 40 available: reserve 15 → 0 + 40 - 15.
        Assert.Equal(WarmConnectionGovernor.Ceiling, WarmConnectionGovernor.Target(2, 0, [Pool(max: 60, available: 40)]));
        Assert.Equal(5, WarmConnectionGovernor.Target(2, 0, [Pool(max: 60, available: 20)]));
    }

    [Fact]
    public async Task Governor_GrowsAndShrinksTheJobBudgetWithLiveCapacity()
    {
        IReadOnlyList<ProviderConnectionSnapshot> providers = [Provider(limit: 40, active: 0)];
        await using var governor = new WarmConnectionGovernor(2, () => providers, TimeSpan.FromMilliseconds(20));
        Assert.Equal(WarmConnectionGovernor.Ceiling, governor.Allowed);

        providers = [Provider(limit: 40, active: 39, waiting: 3)];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (governor.Allowed != 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(2, governor.Allowed);
    }

    private static ProviderConnectionSnapshot Provider(int limit, int active, int waiting = 0) =>
        Pool(limit, Math.Max(0, limit - active), new ProviderConnectionAdmissionSnapshot(
            ConfiguredTransferLimit: limit, EffectiveTransferLimit: limit, BaseMetadataCapacity: 0,
            MetadataBurstAllowance: 0, MaxMetadataCapacity: 0, ActiveTransferOperations: active,
            ActiveMetadataOperations: 0, WaitingTransferOperations: waiting, WaitingMetadataOperations: 0));

    private static ProviderConnectionSnapshot Pool(int max, int available, ProviderConnectionAdmissionSnapshot? admission = null) =>
        new("provider", "news.example", ProviderType.Pooled, LiveConnections: max - available, IdleConnections: 0,
            ActiveConnections: max - available, AvailableConnections: available, PendingSelections: 0,
            new ConnectionPoolChurn(0, 0, 0, 0, 0, 0, 0), LearnedConnectionLimit: null,
            ConfiguredMaxConnections: max, EffectiveMaxConnections: max, admission);
}
