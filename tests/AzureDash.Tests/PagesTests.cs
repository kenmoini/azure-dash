using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class PagesTests
{
    [Fact]
    public async Task Index_renders_runtime_and_controls()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/");
        Assert.Contains("<title>Runtime · azure-dash</title>", html);
        Assert.Contains("hx-get=\"/partials/runtime\"", html);
        Assert.Contains("id=\"controls\"", html);
        Assert.Contains("Hostname", html);
        Assert.Contains("/js/htmx.min.js", html);
    }

    [Fact]
    public async Task Runtime_partial_is_a_fragment()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/partials/runtime");
        Assert.Contains("Hostname", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Htmx_control_post_returns_controls_fragment()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().HxPostFormAsync("/controls/readiness", ("enabled", "false"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("id=\"controls\"", html);
        Assert.Contains("Restore readiness", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Htmx_crash_returns_html_span()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().HxPostFormAsync("/controls/crash", ("exit_code", "3"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("Exiting with code 3", await r.Content.ReadAsStringAsync());
        await f.Exit.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Controls_disabled_message()
    {
        using var f = new AppFactory { Settings = new() { ControlsEnabled = false, ImdsEnabled = false } };
        var html = await f.CreateClient().GetStringAsync("/partials/controls");
        Assert.Contains("Controls are disabled (CONTROLS_ENABLED=false)", html);
        Assert.DoesNotContain("hx-post", html);
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/htmx.min.js")]
    public async Task Static_assets_are_served(string path)
    {
        using var f = new AppFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync(path)).StatusCode);
    }
}
