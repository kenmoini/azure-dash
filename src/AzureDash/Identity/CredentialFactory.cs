using Azure.Core;
using Azure.Identity;
using AzureDash.Configuration;

namespace AzureDash.Identity;

public sealed record CredentialSelection(
    AuthMode Mode, string Reason, TokenCredential Credential, string CredentialType,
    string? ClientId, string? TenantId, Uri AuthorityHost, string? TokenFile);

/// <summary>Explicit credential selection. DefaultAzureCredential is deliberately not used.</summary>
public static class CredentialFactory
{
    public static (AuthMode Mode, string Reason) Resolve(AppSettings settings, EnvLookup env)
    {
        if (settings.AuthMode != AuthMode.Auto) return (settings.AuthMode, $"AUTH_MODE={AuthModes.Name(settings.AuthMode)}");
        if (AppSettings.Blank(env("AZURE_FEDERATED_TOKEN_FILE")) is not null) return (AuthMode.WorkloadIdentity, "auto: AZURE_FEDERATED_TOKEN_FILE is set");
        if (settings.SpiffeJwtFile is not null) return (AuthMode.Spiffe, "auto: SPIFFE_JWT_FILE is set");
        if (AppSettings.Blank(env("AZURE_CLIENT_SECRET")) is not null) return (AuthMode.ClientSecret, "auto: AZURE_CLIENT_SECRET is set");
        return (AuthMode.Dev, "auto: no workload credentials found; using developer credentials (Azure CLI, then azd)");
    }

    public static CredentialSelection Create(AppSettings settings, EnvLookup env)
    {
        var (mode, reason) = Resolve(settings, env);
        var authority = AppSettings.Blank(env("AZURE_AUTHORITY_HOST")) is { } host ? new Uri(host) : CloudEndpoints.For(settings.Cloud).AuthorityHost;
        var clientId = AppSettings.Blank(env("AZURE_CLIENT_ID"));
        var tenantId = AppSettings.Blank(env("AZURE_TENANT_ID"));

        switch (mode)
        {
            case AuthMode.WorkloadIdentity:
            {
                var file = AppSettings.Blank(env("AZURE_FEDERATED_TOKEN_FILE"));
                Require(mode, ("AZURE_CLIENT_ID", clientId), ("AZURE_TENANT_ID", tenantId), ("AZURE_FEDERATED_TOKEN_FILE", file));
                var credential = new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
                {
                    ClientId = clientId, TenantId = tenantId, TokenFilePath = file, AuthorityHost = authority,
                });
                return new(mode, reason, credential, nameof(WorkloadIdentityCredential), clientId, tenantId, authority, file);
            }
            case AuthMode.Spiffe:
            {
                Require(mode, ("AZURE_CLIENT_ID", clientId), ("AZURE_TENANT_ID", tenantId), ("SPIFFE_JWT_FILE", settings.SpiffeJwtFile));
                var source = new SpiffeAssertionSource(settings.SpiffeJwtFile!);
                var credential = new ClientAssertionCredential(tenantId!, clientId!, source.ReadAsync,
                    new ClientAssertionCredentialOptions { AuthorityHost = authority });
                return new(mode, reason, credential, nameof(ClientAssertionCredential), clientId, tenantId, authority, settings.SpiffeJwtFile);
            }
            case AuthMode.ClientSecret:
            {
                var secret = AppSettings.Blank(env("AZURE_CLIENT_SECRET"));
                Require(mode, ("AZURE_CLIENT_ID", clientId), ("AZURE_TENANT_ID", tenantId), ("AZURE_CLIENT_SECRET", secret));
                var credential = new ClientSecretCredential(tenantId!, clientId!, secret!,
                    new ClientSecretCredentialOptions { AuthorityHost = authority });
                return new(mode, reason, credential, nameof(ClientSecretCredential), clientId, tenantId, authority, null);
            }
            default:
            {
                var credential = new ChainedTokenCredential(
                    new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId, AuthorityHost = authority }),
                    new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { TenantId = tenantId, AuthorityHost = authority }));
                return new(AuthMode.Dev, reason, credential, "ChainedTokenCredential(AzureCli, AzureDeveloperCli)", clientId, tenantId, authority, null);
            }
        }
    }

    static void Require(AuthMode mode, params (string Name, string? Value)[] values)
    {
        var missing = values.Where(v => v.Value is null).Select(v => v.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"AUTH_MODE {AuthModes.Name(mode)} requires {string.Join(", ", missing)}");
    }
}
