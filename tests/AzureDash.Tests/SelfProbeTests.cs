using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class SelfProbeTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/healthz/live")]
    [InlineData("9090", "http://127.0.0.1:9090/healthz/live")]
    [InlineData("9090;8081", "http://127.0.0.1:9090/healthz/live")]
    public void Local_url_uses_first_http_port(string? ports, string expected) =>
        Assert.Equal(new Uri(expected), SelfProbe.LocalUrl(name => name == "ASPNETCORE_HTTP_PORTS" ? ports : null));

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Exit_code_follows_status(HttpStatusCode status, int expected)
    {
        var output = new StringWriter();
        var code = await SelfProbe.RunAsync(new Uri("http://127.0.0.1:8080/healthz/live"), StubHandler.Returns("{}", status), output);
        Assert.Equal(expected, code);
        Assert.Contains(((int)status).ToString(), output.ToString());
    }

    [Fact]
    public async Task Connection_failure_is_unhealthy()
    {
        var output = new StringWriter();
        var code = await SelfProbe.RunAsync(new Uri("http://127.0.0.1:8080/healthz/live"),
            new StubHandler((_, _) => throw new HttpRequestException("refused")), output);
        Assert.Equal(1, code);
        Assert.Contains("unhealthy: refused", output.ToString());
    }
}
