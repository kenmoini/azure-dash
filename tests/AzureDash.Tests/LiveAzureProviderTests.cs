using AzureDash.Configuration;
using AzureDash.Identity;
using AzureDash.Inventory;

namespace AzureDash.Tests;

public class LiveAzureProviderTests
{
    [Fact]
    public void Construction_surfaces_credential_errors()
    {
        var creds = new CredentialProvider(() => throw new InvalidOperationException("AUTH_MODE spiffe requires AZURE_CLIENT_ID"), () => (AuthMode.Spiffe, "test"));
        var ex = Assert.Throws<InvalidOperationException>(() => new LiveAzureProvider(creds, new AppSettings()));
        Assert.Contains("AZURE_CLIENT_ID", ex.Message);
    }

    [Fact]
    public void Construction_makes_no_network_calls()
    {
        var creds = CredentialProvider.FromSettings(new AppSettings { AuthMode = AuthMode.Dev }, _ => null);
        _ = new LiveAzureProvider(creds, new AppSettings { SubscriptionId = "00000000-0000-0000-0000-000000000001", Cloud = AzureCloud.UsGov });
    }
}
