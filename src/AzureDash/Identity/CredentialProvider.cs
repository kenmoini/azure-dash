using AzureDash.Configuration;

namespace AzureDash.Identity;

/// <summary>Builds the credential on first use (so misconfiguration shows in panels, not as a startup crash) and caches success.</summary>
public sealed class CredentialProvider(Func<CredentialSelection> create, Func<(AuthMode Mode, string Reason)> describe)
{
    private readonly object _gate = new();
    private CredentialSelection? _selection;

    public static CredentialProvider FromSettings(AppSettings settings, EnvLookup env) =>
        new(() => CredentialFactory.Create(settings, env), () => CredentialFactory.Resolve(settings, env));

    public CredentialSelection Get()
    {
        lock (_gate) return _selection ??= create();
    }

    public (AuthMode Mode, string Reason) Describe() => describe();
}
