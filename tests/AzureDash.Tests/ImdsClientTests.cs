using System.Diagnostics;
using System.Net;
using AzureDash.Configuration;
using AzureDash.Imds;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class ImdsClientTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    const string Compute = """
        {"name":"aks-nodepool1-12345678-vmss_0","location":"eastus","zone":"1","vmSize":"Standard_D4s_v5",
         "subscriptionId":"sub-1","resourceGroupName":"MC_rg-demo_aks-demo_eastus","vmScaleSetName":"aks-nodepool1-12345678-vmss",
         "osType":"Linux","vmId":"vm-id-1","tagsList":[{"name":"x","value":"y"}],"publicKeys":[]}
        """;

    ImdsClient Client(StubHandler handler, AppSettings? settings = null) =>
        new(new HttpClient(handler), settings ?? new AppSettings(), _time);

    [Fact]
    public async Task Returns_compute_fields_and_sends_metadata_header()
    {
        var handler = StubHandler.Returns(Compute);
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.True(info.Available);
        Assert.Equal("MC_rg-demo_aks-demo_eastus", info.Compute!["resourceGroupName"]);
        Assert.Equal("aks-nodepool1-12345678-vmss", info.Compute["vmScaleSetName"]);
        Assert.False(info.Compute.ContainsKey("tagsList"));
        var request = handler.Requests.Single();
        Assert.Equal("true", request.Headers.GetValues("Metadata").Single());
        Assert.Equal("http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01&format=json", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Hanging_endpoint_times_out_quickly()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var sw = Stopwatch.StartNew();
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("did not answer within 1s", info.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Http_error_is_unavailable()
    {
        var info = await Client(StubHandler.Returns("{}", HttpStatusCode.BadRequest)).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.StartsWith("IMDS returned 400", info.Reason);
    }

    [Theory]
    [InlineData("<html>proxy</html>")]
    [InlineData("[1,2]")]
    public async Task Non_json_or_non_object_is_unavailable(string body)
    {
        var info = await Client(StubHandler.Returns(body)).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("not a JSON object", info.Reason);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("Connection refused", info.Reason);
    }

    [Fact]
    public async Task Disabled_makes_no_request()
    {
        var handler = StubHandler.Returns(Compute);
        var info = await Client(handler, new AppSettings { ImdsEnabled = false }).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Equal("disabled (IMDS_ENABLED=false)", info.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caches_for_ttl()
    {
        var handler = StubHandler.Returns(Compute);
        var client = Client(handler, new AppSettings { CacheTtl = TimeSpan.FromSeconds(60) });
        await client.GetAsync(CancellationToken.None);
        await client.GetAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        _time.Advance(TimeSpan.FromSeconds(61));
        await client.GetAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }
}
