using Azure.Core;
using AzureDash.Configuration;
using AzureDash.Inventory;

namespace AzureDash.Identity;

public sealed record IdentityInfo(
    string Mode, string Reason, string? CredentialType, string? ClientId, string? TenantId, string? AuthorityHost,
    string? TokenFile, JwtSummary? FederatedToken, JwtSummary? AccessToken, string? Error, DateTimeOffset FetchedAt);

/// <summary>Explains the workload identity chain: which credential, what the cluster presented, what Entra returned.</summary>
public sealed class IdentityInfoService(CredentialProvider credentials, AppSettings settings, TimeProvider time)
{
    public async Task<IdentityInfo> GetAsync(CancellationToken ct)
    {
        var (mode, reason) = credentials.Describe();
        CredentialSelection selection;
        try
        {
            selection = credentials.Get();
        }
        catch (Exception ex)
        {
            return new(AuthModes.Name(mode), reason, null, null, null, null, null, null, null, AzureError.From(ex).Message, time.GetUtcNow());
        }

        var federated = selection.TokenFile is null ? null : await ReadFederatedAsync(selection, ct);
        JwtSummary? access = null;
        string? error = null;
        try
        {
            var token = await selection.Credential.GetTokenAsync(new TokenRequestContext([CloudEndpoints.For(settings.Cloud).ArmScope]), ct);
            access = JwtDisplay.Decode(token.Token, JwtDisplay.AccessTokenClaims);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = AzureError.From(ex).Message;
        }

        return new(AuthModes.Name(selection.Mode), selection.Reason, selection.CredentialType, selection.ClientId, selection.TenantId,
            selection.AuthorityHost.ToString(), selection.TokenFile, federated, access, error, time.GetUtcNow());
    }

    static async Task<JwtSummary> ReadFederatedAsync(CredentialSelection selection, CancellationToken ct)
    {
        var path = selection.TokenFile!;
        try
        {
            var raw = selection.Mode == AuthMode.Spiffe
                ? await new SpiffeAssertionSource(path).ReadAsync(ct)
                : await File.ReadAllTextAsync(path, ct);
            return JwtDisplay.Decode(raw, JwtDisplay.FederatedClaims);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return JwtSummary.Failed($"could not read {path}: {AzureError.FirstLine(ex.Message)}");
        }
    }
}
