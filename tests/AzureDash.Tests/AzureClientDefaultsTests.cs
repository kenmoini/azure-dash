using Azure.ResourceManager;
using AzureDash.Identity;

namespace AzureDash.Tests;

public class AzureClientDefaultsTests
{
    [Fact]
    public void Apply_sets_network_timeout_and_max_retries()
    {
        var options = new ArmClientOptions();
        AzureClientDefaults.Apply(options.Retry);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Retry.NetworkTimeout);
        Assert.Equal(2, options.Retry.MaxRetries);
    }

    [Fact]
    public void Process_timeout_constant_is_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), AzureClientDefaults.ProcessTimeout);
    }
}
