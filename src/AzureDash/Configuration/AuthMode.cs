namespace AzureDash.Configuration;

public enum AuthMode { Auto, WorkloadIdentity, Spiffe, ClientSecret, Dev }

public static class AuthModes
{
    public static string Name(AuthMode mode) => mode switch
    {
        AuthMode.WorkloadIdentity => "workload-identity",
        AuthMode.Spiffe => "spiffe",
        AuthMode.ClientSecret => "client-secret",
        AuthMode.Dev => "dev",
        _ => "auto",
    };

    public static AuthMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => AuthMode.Auto,
        "workload-identity" => AuthMode.WorkloadIdentity,
        "spiffe" => AuthMode.Spiffe,
        "client-secret" => AuthMode.ClientSecret,
        "dev" => AuthMode.Dev,
        _ => null,
    };
}
