using System.Net;
using System.Text.RegularExpressions;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class AzureApiTests
{
    [Fact]
    public async Task Api_returns_items_and_metadata()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/azure/vms")).JsonAsync();
        Assert.Equal("vms", json.GetProperty("kind").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("error").ValueKind);
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal("aks-nodepool1-12345678-vmss", json.GetProperty("items")[1].GetProperty("name").GetString());
        Assert.StartsWith("2026-01-01T00:00:00", json.GetProperty("fetched_at").GetString());
    }

    [Fact]
    public async Task Subscription_items_is_an_object()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/azure/subscription")).JsonAsync();
        Assert.Equal("Demo Subscription", json.GetProperty("items").GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Unknown_kind_is_404()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/azure/buckets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/partials/azure/buckets")).StatusCode);
    }

    [Fact]
    public async Task Failing_kind_reports_error_and_other_kinds_still_work()
    {
        using var f = new AppFactory();
        f.Azure.Fail("storageaccounts");
        var client = f.CreateClient();
        var bad = await (await client.GetAsync("/api/azure/storageaccounts")).JsonAsync();
        Assert.Contains("AuthorizationFailed", bad.GetProperty("error").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bad.GetProperty("items").ValueKind);
        var good = await (await client.GetAsync("/api/azure/vnets")).JsonAsync();
        Assert.Equal(1, good.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Cache_refresh_and_invalidate()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        await client.GetAsync("/api/azure/vnets");
        await client.GetAsync("/api/azure/vnets");
        Assert.Equal(1, f.Azure.Calls["vnets"]);
        await client.GetAsync("/api/azure/vnets?refresh=true");
        Assert.Equal(2, f.Azure.Calls["vnets"]);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/azure/refresh", null)).StatusCode);
        await client.GetAsync("/api/azure/vnets");
        Assert.Equal(3, f.Azure.Calls["vnets"]);
    }

    [Fact]
    public async Task Partial_renders_table_fragment()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/partials/azure/storageaccounts");
        Assert.Contains("<h2>Storage accounts</h2>", html);
        Assert.Contains("stdemo001", html);
        Assert.Contains("Fetched 2026-01-01 00:00:00 UTC", html);
        Assert.Contains("hx-get=\"/partials/azure/storageaccounts?refresh=1\"", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Partial_shows_error_banner()
    {
        using var f = new AppFactory();
        f.Azure.Fail("nsgrules");
        var html = await f.CreateClient().GetStringAsync("/partials/azure/nsgrules");
        Assert.Contains("class=\"error\"", html);
        Assert.Contains("AuthorizationFailed", html);
    }

    [Fact]
    public async Task Partial_shows_stale_data_after_failed_refresh()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        await client.GetStringAsync("/partials/azure/vms");
        f.Azure.Fail("vms");
        var html = await client.GetStringAsync("/partials/azure/vms?refresh=1");
        Assert.Contains("showing previously cached data", html);
        Assert.Contains("vm-jump", html);
        Assert.Contains("class=\"error\"", html);
    }

    [Theory]
    [InlineData("resourcegroups", "No resource groups")]
    [InlineData("vms", "No virtual machines or scale sets")]
    [InlineData("storageaccounts", "No storage accounts")]
    [InlineData("vnets", "No virtual networks")]
    [InlineData("subnets", "No subnets")]
    [InlineData("nsgrules", "No custom NSG rules")]
    [InlineData("resourcegraph", "No resources")]
    public async Task Empty_lists_say_so(string kind, string message)
    {
        using var f = new AppFactory();
        f.Azure.EmptyLists = true;
        Assert.Contains(message, await f.CreateClient().GetStringAsync($"/partials/azure/{kind}"));
    }

    [Fact]
    public async Task Every_kind_renders()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Contains("Demo Subscription", await client.GetStringAsync("/partials/azure/subscription"));
        Assert.Contains("MC_rg-demo_aks-demo_eastus", await client.GetStringAsync("/partials/azure/resourcegroups"));
        Assert.Contains("3 instances", await client.GetStringAsync("/partials/azure/vms"));
        Assert.Contains("10.0.0.0/16", await client.GetStringAsync("/partials/azure/vnets"));
        Assert.Contains("rt-aks", await client.GetStringAsync("/partials/azure/subnets"));
        Assert.Contains("allow-https", await client.GetStringAsync("/partials/azure/nsgrules"));
        Assert.Contains("microsoft.compute/virtualmachines", await client.GetStringAsync("/partials/azure/resourcegraph"));
    }

    [Fact]
    public async Task Azure_page_has_one_lazy_panel_per_kind()
    {
        using var f = new AppFactory { Settings = new() { ImdsEnabled = false, SubscriptionId = "sub-123", ResourceGroup = "rg-x" } };
        var html = await f.CreateClient().GetStringAsync("/azure");
        Assert.Equal(8, Regex.Matches(html, "data-azure-panel").Count);
        Assert.Contains("hx-post=\"/api/azure/refresh\"", html);
        Assert.Contains("sub-123", html);
        Assert.Contains("rg-x", html);
        Assert.Empty(f.Azure.Calls);
    }
}
