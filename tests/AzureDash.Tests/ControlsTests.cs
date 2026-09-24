using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class ControlsTests
{
    [Fact]
    public async Task State_uses_snake_case()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/state")).JsonAsync();
        Assert.True(json.GetProperty("live").GetBoolean());
        Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.True(json.GetProperty("controls_enabled").GetBoolean());
        Assert.Equal(0, json.GetProperty("uptime_seconds").GetDouble());
        Assert.False(json.GetProperty("cpu_load").GetProperty("active").GetBoolean());
    }

    [Theory]
    [InlineData("/controls/readiness", "/healthz/ready", "ready")]
    [InlineData("/controls/liveness", "/healthz/live", "live")]
    public async Task Toggle_flips_probe(string control, string probe, string key)
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        var r = await client.PostFormAsync(control, ("enabled", "false"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await r.JsonAsync()).GetProperty(key).GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(probe)).StatusCode);

        await client.PostFormAsync(control, ("enabled", "true"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(probe)).StatusCode);
    }

    [Theory]
    [InlineData("/controls/readiness")]
    [InlineData("/controls/cpu")]
    public async Task Missing_or_invalid_enabled_is_422(string control)
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostFormAsync(control)).StatusCode);
        var r = await client.PostFormAsync(control, ("enabled", "perhaps"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("enabled", (await r.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Cpu_start_clamps_and_stop()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        var on = await (await client.PostFormAsync("/controls/cpu", ("enabled", "true"), ("workers", "5"))).JsonAsync();
        Assert.True(on.GetProperty("cpu_load").GetProperty("active").GetBoolean());
        Assert.Equal(2, on.GetProperty("cpu_load").GetProperty("workers").GetInt32());
        var off = await (await client.PostFormAsync("/controls/cpu", ("enabled", "false"))).JsonAsync();
        Assert.False(off.GetProperty("cpu_load").GetProperty("active").GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("abc")]
    public async Task Cpu_invalid_workers_is_422(string workers)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/cpu", ("enabled", "true"), ("workers", workers));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("workers", (await r.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Crash_returns_202_then_exits_with_code()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/crash", ("exit_code", "7"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(7, (await r.JsonAsync()).GetProperty("exit_code").GetInt32());
        Assert.Equal(7, await f.Exit.Exited.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("x")]
    public async Task Crash_invalid_exit_code_is_422_and_does_not_exit(string code)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/crash", ("exit_code", code));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        await Task.Delay(700);
        Assert.False(f.Exit.Exited.IsCompleted);
    }

    [Fact]
    public async Task Controls_disabled_returns_403_but_state_still_works()
    {
        using var f = new AppFactory { Settings = new() { ControlsEnabled = false, ImdsEnabled = false } };
        var client = f.CreateClient();
        var r = await client.PostFormAsync("/controls/liveness", ("enabled", "false"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("controls are disabled (CONTROLS_ENABLED=false)", (await r.JsonAsync()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz/live")).StatusCode);
        Assert.False((await (await client.GetAsync("/api/state")).JsonAsync()).GetProperty("controls_enabled").GetBoolean());
    }
}
