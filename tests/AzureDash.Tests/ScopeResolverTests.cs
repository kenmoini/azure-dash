using AzureDash.Inventory;

namespace AzureDash.Tests;

public class ScopeResolverTests
{
    [Fact]
    public void Configured_subscription_wins()
    {
        var s = ScopeResolver.Choose("sub-cfg", ["sub-a", "sub-b"], null);
        Assert.Equal(new ResolvedScope("sub-cfg", "AZURE_SUBSCRIPTION_ID", null), s);
    }

    [Fact]
    public void Single_visible_subscription_is_used()
    {
        var s = ScopeResolver.Choose(null, ["sub-a"], null);
        Assert.Equal(new ResolvedScope("sub-a", "only subscription visible to this identity", null), s);
    }

    [Fact]
    public void Several_visible_picks_first_sorted_and_warns()
    {
        var s = ScopeResolver.Choose(null, ["sub-b", "sub-a", "sub-c"], null);
        Assert.Equal("sub-a", s.SubscriptionId);
        Assert.Equal("first of 3 visible subscriptions", s.Source);
        Assert.Equal("3 subscriptions are visible to this identity; showing sub-a. Set AZURE_SUBSCRIPTION_ID to choose one.", s.Warning);
    }

    [Fact]
    public void None_visible_with_resource_group_scope_asks_for_subscription_id()
    {
        var ex = Assert.Throws<AzureError>(() => ScopeResolver.Choose(null, [], "rg-demo"));
        Assert.Contains("AZURE_RESOURCE_GROUP=rg-demo", ex.Message);
        Assert.Contains("set AZURE_SUBSCRIPTION_ID", ex.Message);
    }

    [Fact]
    public void None_visible_without_resource_group_asks_for_reader()
    {
        var ex = Assert.Throws<AzureError>(() => ScopeResolver.Choose(null, [], null));
        Assert.Contains("grant Reader", ex.Message);
        Assert.Contains("AZURE_SUBSCRIPTION_ID", ex.Message);
    }
}
