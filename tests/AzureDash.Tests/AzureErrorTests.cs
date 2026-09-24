using Azure;
using Azure.Identity;
using AzureDash.Inventory;

namespace AzureDash.Tests;

public class AzureErrorTests
{
    [Fact]
    public void Forbidden_hints_reader_role_and_propagation()
    {
        var e = AzureError.From(new RequestFailedException(403, "The client does not have authorization\nheaders...", "AuthorizationFailed", null));
        Assert.StartsWith("RequestFailedException: AuthorizationFailed (403).", e.Message);
        Assert.Contains("Reader", e.Message);
        Assert.Contains("10 minutes", e.Message);
    }

    [Fact]
    public void Unauthorized_hints_tenant_and_cloud()
    {
        var e = AzureError.From(new RequestFailedException(401, "nope", "InvalidAuthenticationToken", null));
        Assert.Contains("(401)", e.Message);
        Assert.Contains("AZURE_CLOUD", e.Message);
    }

    [Fact]
    public void Other_request_failures_use_first_line()
    {
        var e = AzureError.From(new RequestFailedException(500, "Server broke\nStatus: 500\nContent: ...", "InternalError", null));
        Assert.Equal("RequestFailedException: InternalError (500): Server broke", e.Message);
    }

    [Fact]
    public void Authentication_failure_extracts_aadsts_code_from_later_lines()
    {
        var inner = new AuthenticationFailedException(
            "WorkloadIdentityCredential authentication failed: A configuration issue is preventing authentication.\n" +
            "AADSTS700213: No matching federated identity record found for presented assertion subject 'system:serviceaccount:x:y'.\nTrace ID: 1");
        var e = AzureError.From(inner);
        Assert.StartsWith("AuthenticationFailedException: WorkloadIdentityCredential authentication failed", e.Message);
        Assert.Contains("AADSTS700213: No matching federated identity record found", e.Message);
        Assert.Contains("federated identity credential", e.Message);
        Assert.DoesNotContain("Trace ID", e.Message);
    }

    [Fact]
    public void Credential_unavailable_is_named()
    {
        var e = AzureError.From(new CredentialUnavailableException("Azure CLI not installed"));
        Assert.Equal("CredentialUnavailableException: Azure CLI not installed", e.Message);
    }

    [Fact]
    public void Generic_exception_and_passthrough()
    {
        Assert.Equal("InvalidOperationException: boom", AzureError.From(new InvalidOperationException("boom\nmore")).Message);
        var original = new AzureError("already translated");
        Assert.Same(original, AzureError.From(original));
    }
}
