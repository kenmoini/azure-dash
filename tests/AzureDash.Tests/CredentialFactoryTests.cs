using Azure.Identity;
using AzureDash.Configuration;
using AzureDash.Identity;

namespace AzureDash.Tests;

public class CredentialFactoryTests
{
    static EnvLookup Env(params (string Key, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return name => d.GetValueOrDefault(name);
    }

    static readonly (string, string)[] Ids = [("AZURE_CLIENT_ID", "cid"), ("AZURE_TENANT_ID", "tid")];

    [Fact]
    public void Explicit_mode_wins()
    {
        var (mode, reason) = CredentialFactory.Resolve(new AppSettings { AuthMode = AuthMode.ClientSecret },
            Env(("AZURE_FEDERATED_TOKEN_FILE", "/t")));
        Assert.Equal(AuthMode.ClientSecret, mode);
        Assert.Equal("AUTH_MODE=client-secret", reason);
    }

    [Fact]
    public void Auto_prefers_workload_identity_then_spiffe_then_secret_then_dev()
    {
        var all = new AppSettings { SpiffeJwtFile = "/s" };
        Assert.Equal(AuthMode.WorkloadIdentity, CredentialFactory.Resolve(all, Env(("AZURE_FEDERATED_TOKEN_FILE", "/t"), ("AZURE_CLIENT_SECRET", "x"))).Mode);
        Assert.Equal(AuthMode.Spiffe, CredentialFactory.Resolve(all, Env(("AZURE_CLIENT_SECRET", "x"))).Mode);
        Assert.Equal(AuthMode.ClientSecret, CredentialFactory.Resolve(new AppSettings(), Env(("AZURE_CLIENT_SECRET", "x"))).Mode);
        var (dev, reason) = CredentialFactory.Resolve(new AppSettings(), Env());
        Assert.Equal(AuthMode.Dev, dev);
        Assert.StartsWith("auto:", reason);
    }

    [Fact]
    public void Workload_identity_selection()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env([.. Ids, ("AZURE_FEDERATED_TOKEN_FILE", "/var/run/secrets/azure/tokens/azure-identity-token")]));
        Assert.Equal(AuthMode.WorkloadIdentity, s.Mode);
        Assert.IsType<WorkloadIdentityCredential>(s.Credential);
        Assert.Equal("WorkloadIdentityCredential", s.CredentialType);
        Assert.Equal("cid", s.ClientId);
        Assert.Equal("tid", s.TenantId);
        Assert.Equal("/var/run/secrets/azure/tokens/azure-identity-token", s.TokenFile);
        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud, s.AuthorityHost);
    }

    [Fact]
    public void Workload_identity_missing_values_names_them()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialFactory.Create(
            new AppSettings { AuthMode = AuthMode.WorkloadIdentity }, Env(("AZURE_CLIENT_ID", "cid"))));
        Assert.Equal("AUTH_MODE workload-identity requires AZURE_TENANT_ID, AZURE_FEDERATED_TOKEN_FILE", ex.Message);
    }

    [Fact]
    public void Spiffe_selection_uses_client_assertion()
    {
        var s = CredentialFactory.Create(new AppSettings { SpiffeJwtFile = "/var/run/secrets/azure/token" }, Env(Ids));
        Assert.Equal(AuthMode.Spiffe, s.Mode);
        Assert.IsType<ClientAssertionCredential>(s.Credential);
        Assert.Equal("/var/run/secrets/azure/token", s.TokenFile);
    }

    [Fact]
    public void Client_secret_selection()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env([.. Ids, ("AZURE_CLIENT_SECRET", "shh")]));
        Assert.IsType<ClientSecretCredential>(s.Credential);
        Assert.Null(s.TokenFile);
    }

    [Fact]
    public void Dev_selection_is_a_chain()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env());
        Assert.Equal(AuthMode.Dev, s.Mode);
        Assert.IsType<ChainedTokenCredential>(s.Credential);
    }

    [Fact]
    public void Authority_host_env_overrides_cloud_default()
    {
        Assert.Equal(AzureAuthorityHosts.AzureGovernment,
            CredentialFactory.Create(new AppSettings { Cloud = AzureCloud.UsGov }, Env()).AuthorityHost);
        Assert.Equal(new Uri("https://login.example.test/"),
            CredentialFactory.Create(new AppSettings(), Env(("AZURE_AUTHORITY_HOST", "https://login.example.test/"))).AuthorityHost);
    }

    [Fact]
    public void Cloud_endpoints()
    {
        Assert.Equal("https://management.azure.com/.default", CloudEndpoints.For(AzureCloud.Public).ArmScope);
        Assert.Equal("https://management.usgovcloudapi.net/.default", CloudEndpoints.For(AzureCloud.UsGov).ArmScope);
        Assert.Equal("https://management.chinacloudapi.cn/.default", CloudEndpoints.For(AzureCloud.China).ArmScope);
    }

    [Fact]
    public void Provider_caches_success_and_retries_failure()
    {
        var calls = 0;
        var ok = CredentialFactory.Create(new AppSettings(), Env());
        var provider = new CredentialProvider(() => ++calls == 1 ? throw new InvalidOperationException("not yet") : ok, () => (AuthMode.Dev, "test"));
        Assert.Throws<InvalidOperationException>(provider.Get);
        Assert.Same(ok, provider.Get());
        Assert.Same(ok, provider.Get());
        Assert.Equal(2, calls);
        Assert.Equal((AuthMode.Dev, "test"), provider.Describe());
    }
}
