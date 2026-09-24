using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;

namespace AzureDash.Inventory;

/// <summary>A user-facing, single-line Azure failure with an actionable hint where one is known.</summary>
public sealed partial class AzureError(string message, Exception? inner = null) : Exception(message, inner)
{
    public static AzureError From(Exception ex) => ex switch
    {
        AzureError a => a,
        RequestFailedException { Status: 403 } r => new(
            $"RequestFailedException: {r.ErrorCode ?? "Forbidden"} (403). Grant the identity Reader on the subscription or resource group; new role assignments can take up to 10 minutes to apply.", ex),
        RequestFailedException { Status: 401 } r => new(
            $"RequestFailedException: {r.ErrorCode ?? "Unauthorized"} (401). The token was rejected; check AZURE_TENANT_ID and the target cloud (AZURE_CLOUD).", ex),
        RequestFailedException r => new(
            $"RequestFailedException: {r.ErrorCode ?? "Error"} ({r.Status}): {FirstLine(r.Message)}", ex),
        CredentialUnavailableException => new($"CredentialUnavailableException: {FirstLine(ex.Message)}", ex),
        AuthenticationFailedException => new(
            $"AuthenticationFailedException: {FirstLine(ex.Message)}{AadCode(ex.Message)} — check the federated identity credential " +
            "(issuer, subject, audience api://AzureADTokenExchange) and AZURE_CLIENT_ID/AZURE_TENANT_ID; new federated credentials can take a few minutes to propagate.", ex),
        _ => new($"{ex.GetType().Name}: {FirstLine(ex.Message)}", ex),
    };

    internal static string FirstLine(string text)
    {
        var i = text.IndexOfAny(['\r', '\n']);
        return (i < 0 ? text : text[..i]).Trim();
    }

    static string AadCode(string text)
    {
        var m = AadstsPattern().Match(text);
        return m.Success && !FirstLine(text).Contains(m.Value, StringComparison.Ordinal) ? " " + m.Value.Trim() : "";
    }

    [GeneratedRegex(@"AADSTS\d+:[^\r\n]*")]
    private static partial Regex AadstsPattern();
}
