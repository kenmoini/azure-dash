using Azure.Core;

namespace AzureDash.Identity;

/// <summary>
/// Network timeout/retry bounds applied to every Azure SDK client and credential option, so blocked egress fails
/// fast (a few seconds) instead of hanging on Azure.Core's defaults (100 s network timeout, 3 retries w/ backoff).
/// </summary>
public static class AzureClientDefaults
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(10);
    public const int MaxRetries = 2;

    /// <summary>Applied to AzureCliCredentialOptions/AzureDeveloperCliCredentialOptions.ProcessTimeout.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    public static void Apply(RetryOptions retry)
    {
        retry.NetworkTimeout = NetworkTimeout;
        retry.MaxRetries = MaxRetries;
    }
}
