using System.Net;
using AzureDash.State;
using AzureDash.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AzureDash.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    public async Task Probes_are_healthy_by_default(string path)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_returns_503_when_disabled()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        f.Services.GetRequiredService<RuntimeState>().Live = false;
        var r = await client.GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("{\"status\":\"failing\",\"reason\":\"liveness disabled via controls\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_returns_503_when_disabled()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        f.Services.GetRequiredService<RuntimeState>().Ready = false;
        var r = await client.GetAsync("/healthz/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("readiness disabled via controls", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Started_at_comes_from_the_time_provider()
    {
        using var f = new AppFactory();
        _ = f.CreateClient();
        Assert.Equal(f.Time.Now, f.Services.GetRequiredService<RuntimeState>().StartedAt);
    }
}
