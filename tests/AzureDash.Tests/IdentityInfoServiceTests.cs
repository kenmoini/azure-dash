using Azure.Identity;
using AzureDash.Configuration;
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class IdentityInfoServiceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("identity-").FullName;
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public void Dispose() => Directory.Delete(_dir, true);

    string TokenFile(string content)
    {
        var path = Path.Combine(_dir, "token");
        File.WriteAllText(path, content);
        return path;
    }

    static readonly string AccessJwt = TestJwt.Make(new
    {
        aud = "https://management.azure.com", iss = "https://sts.windows.net/tid/", oid = "object-id-1", tid = "tid",
        appid = "cid", idtyp = "app", ver = "1.0", xms_mirid = "/subscriptions/s/resourcegroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-azure-dash",
        exp = 1767232800,
    });

    IdentityInfoService Service(AuthMode mode, string? tokenFile, FakeTokenCredential credential, AzureCloud cloud = AzureCloud.Public) =>
        new(new CredentialProvider(
                () => new CredentialSelection(mode, "test reason", credential, "WorkloadIdentityCredential", "cid", "tid",
                    new Uri("https://login.microsoftonline.com/"), tokenFile),
                () => (mode, "test reason")),
            new AppSettings { Cloud = cloud }, _time);

    [Fact]
    public async Task Shows_federated_and_access_token_claims()
    {
        var federated = TestJwt.Make(new { iss = "https://oidc.example/", sub = "system:serviceaccount:azure-dash:azure-dash", aud = "api://AzureADTokenExchange", exp = 1767229200 });
        var credential = new FakeTokenCredential(() => AccessJwt);
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile(federated), credential).GetAsync(CancellationToken.None);

        Assert.Null(info.Error);
        Assert.Equal("workload-identity", info.Mode);
        Assert.Equal("cid", info.ClientId);
        Assert.Equal("system:serviceaccount:azure-dash:azure-dash", info.FederatedToken!.Claims["sub"]);
        Assert.Equal("object-id-1", info.AccessToken!.Claims["oid"]);
        Assert.EndsWith("id-azure-dash", info.AccessToken.Claims["xms_mirid"]);
        Assert.Equal(new[] { "https://management.azure.com/.default" }, credential.Requests.Single());
        Assert.Equal(_time.Now, info.FetchedAt);
    }

    [Fact]
    public async Task Requests_the_sovereign_cloud_scope()
    {
        var credential = new FakeTokenCredential(() => AccessJwt);
        await Service(AuthMode.ClientSecret, null, credential, AzureCloud.China).GetAsync(CancellationToken.None);
        Assert.Equal(new[] { "https://management.chinacloudapi.cn/.default" }, credential.Requests.Single());
    }

    [Fact]
    public async Task Spiffe_mode_reads_through_the_assertion_source()
    {
        var jwt = TestJwt.Make(new { sub = "spiffe://td/ns/azure-dash-ztwim/sa/azure-dash" });
        var info = await Service(AuthMode.Spiffe, TokenFile(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jwt))),
            new FakeTokenCredential(() => AccessJwt)).GetAsync(CancellationToken.None);
        Assert.Equal("spiffe://td/ns/azure-dash-ztwim/sa/azure-dash", info.FederatedToken!.Claims["sub"]);
    }

    [Fact]
    public async Task Garbage_token_file_is_a_decode_error_and_access_token_still_attempted()
    {
        var credential = new FakeTokenCredential(() => AccessJwt);
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile("definitely not a jwt"), credential).GetAsync(CancellationToken.None);
        Assert.Contains("could not decode token", info.FederatedToken!.Error);
        Assert.Equal("object-id-1", info.AccessToken!.Claims["oid"]);
    }

    [Fact]
    public async Task Missing_token_file_is_reported_not_thrown()
    {
        var info = await Service(AuthMode.WorkloadIdentity, Path.Combine(_dir, "absent"), new FakeTokenCredential(() => AccessJwt))
            .GetAsync(CancellationToken.None);
        Assert.StartsWith("could not read", info.FederatedToken!.Error);
    }

    [Fact]
    public async Task Token_exchange_failure_is_translated()
    {
        var credential = new FakeTokenCredential(() => throw new AuthenticationFailedException(
            "WorkloadIdentityCredential authentication failed\nAADSTS700213: No matching federated identity record found for presented assertion subject."));
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile(TestJwt.Make(new { sub = "s" })), credential).GetAsync(CancellationToken.None);
        Assert.Contains("AADSTS700213", info.Error);
        Assert.Null(info.AccessToken);
        Assert.Equal("s", info.FederatedToken!.Claims["sub"]);
    }

    [Fact]
    public async Task Credential_construction_failure_is_reported()
    {
        var service = new IdentityInfoService(
            new CredentialProvider(() => throw new InvalidOperationException("AUTH_MODE spiffe requires AZURE_CLIENT_ID"), () => (AuthMode.Spiffe, "AUTH_MODE=spiffe")),
            new AppSettings(), _time);
        var info = await service.GetAsync(CancellationToken.None);
        Assert.Equal("spiffe", info.Mode);
        Assert.Equal("InvalidOperationException: AUTH_MODE spiffe requires AZURE_CLIENT_ID", info.Error);
        Assert.Null(info.CredentialType);
    }
}
