using AzureDash.Configuration;
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class IdentityPagesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("identity-pages-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static readonly string Federated = TestJwt.Make(new { iss = "https://oidc.example/", sub = "system:serviceaccount:azure-dash:azure-dash", aud = "api://AzureADTokenExchange" });
    static readonly string Access = TestJwt.Make(new { oid = "object-id-1", tid = "tid", appid = "cid" });

    AppFactory Factory()
    {
        var file = Path.Combine(_dir, "token");
        File.WriteAllText(file, Federated);
        var selection = new CredentialSelection(AuthMode.WorkloadIdentity, "auto: AZURE_FEDERATED_TOKEN_FILE is set",
            new FakeTokenCredential(() => Access), "WorkloadIdentityCredential", "cid", "tid", new Uri("https://login.microsoftonline.com/"), file);
        return new AppFactory { Credentials = new CredentialProvider(() => selection, () => (selection.Mode, selection.Reason)) };
    }

    [Fact]
    public async Task Identity_page_has_lazy_panels()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/identity");
        Assert.Contains("hx-get=\"/partials/identity\"", html);
        Assert.Contains("hx-get=\"/partials/imds\"", html);
    }

    [Fact]
    public async Task Identity_partial_shows_claims_but_never_raw_tokens()
    {
        using var f = Factory();
        var html = await f.CreateClient().GetStringAsync("/partials/identity");
        Assert.Contains("workload-identity", html);
        Assert.Contains("system:serviceaccount:azure-dash:azure-dash", html);
        Assert.Contains("object-id-1", html);
        Assert.DoesNotContain(Federated, html);
        Assert.DoesNotContain(Access, html);
        Assert.DoesNotContain(TestJwt.Signature, html);
    }

    [Fact]
    public async Task Identity_api_json_never_contains_raw_tokens()
    {
        using var f = Factory();
        var r = await f.CreateClient().GetAsync("/api/identity");
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestJwt.Signature, text);
        var json = await r.JsonAsync();
        Assert.Equal("system:serviceaccount:azure-dash:azure-dash", json.GetProperty("federated_token").GetProperty("claims").GetProperty("sub").GetString());
        Assert.Equal("object-id-1", json.GetProperty("access_token").GetProperty("claims").GetProperty("oid").GetString());
    }

    [Fact]
    public async Task Identity_partial_renders_errors_with_200()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().GetAsync("/partials/identity");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no credentials configured in tests", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Imds_partial_when_disabled()
    {
        using var f = new AppFactory();
        Assert.Contains("Unavailable: disabled (IMDS_ENABLED=false)", await f.CreateClient().GetStringAsync("/partials/imds"));
        var json = await (await f.CreateClient().GetAsync("/api/imds")).JsonAsync();
        Assert.False(json.GetProperty("available").GetBoolean());
    }
}
