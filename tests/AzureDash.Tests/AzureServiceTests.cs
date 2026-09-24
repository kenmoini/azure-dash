using AzureDash.Inventory;
using AzureDash.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureDash.Tests;

public class AzureServiceTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    AzureService Service(Func<IAzureProvider> factory) =>
        new(factory, new TtlCache(TimeSpan.FromSeconds(60), _time), NullLogger<AzureService>.Instance);

    [Fact]
    public async Task Provider_is_created_lazily_and_retried_after_failure()
    {
        var created = 0;
        var fake = new FakeAzureProvider();
        var service = Service(() => ++created == 1 ? throw new InvalidOperationException("AUTH_MODE workload-identity requires AZURE_CLIENT_ID") : fake);
        Assert.Equal(0, created);

        var first = await service.FetchAsync("vms", force: false, CancellationToken.None);
        Assert.Null(first.Value);
        Assert.Equal("InvalidOperationException: AUTH_MODE workload-identity requires AZURE_CLIENT_ID", first.Error);

        var second = await service.FetchAsync("vms", force: true, CancellationToken.None);
        Assert.Null(second.Error);
        Assert.Equal(2, ((IReadOnlyList<VmInfo>)second.Value!).Count);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task Every_kind_maps_to_a_provider_call()
    {
        var fake = new FakeAzureProvider();
        var service = Service(() => fake);
        foreach (var (kind, _) in AzureKinds.All)
        {
            var entry = await service.FetchAsync(kind, false, CancellationToken.None);
            Assert.Null(entry.Error);
            Assert.NotNull(entry.Value);
            Assert.Equal(1, fake.Calls[kind]);
        }
    }

    [Fact]
    public async Task Provider_failure_is_captured_not_thrown()
    {
        var fake = new FakeAzureProvider();
        fake.Fail("storageaccounts");
        var entry = await Service(() => fake).FetchAsync("storageaccounts", false, CancellationToken.None);
        Assert.StartsWith("RequestFailedException: AuthorizationFailed (403)", entry.Error);
    }
}
