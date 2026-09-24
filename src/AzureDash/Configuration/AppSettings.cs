using System.Globalization;

namespace AzureDash.Configuration;

public sealed record AppSettings
{
    public AuthMode AuthMode { get; init; } = AuthMode.Auto;
    public string? SubscriptionId { get; init; }
    public string? ResourceGroup { get; init; }
    public AzureCloud Cloud { get; init; } = AzureCloud.Public;
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(60);
    public bool ControlsEnabled { get; init; } = true;
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
    public bool AzureDebug { get; init; }
    public string? SpiffeJwtFile { get; init; }
    public bool ImdsEnabled { get; init; } = true;
    public string ImdsApiVersion { get; init; } = "2021-02-01";

    public static AppSettings FromEnvironment(EnvLookup env) => new()
    {
        AuthMode = Blank(env("AUTH_MODE")) is { } mode
            ? AuthModes.Parse(mode) ?? throw Invalid("AUTH_MODE", mode, "auto, workload-identity, spiffe, client-secret or dev")
            : AuthMode.Auto,
        SubscriptionId = Blank(env("AZURE_SUBSCRIPTION_ID")),
        ResourceGroup = Blank(env("AZURE_RESOURCE_GROUP")),
        Cloud = ParseCloud(env("AZURE_CLOUD")),
        CacheTtl = TimeSpan.FromSeconds(ParseNonNegativeInt("AZURE_CACHE_TTL_SECONDS", env("AZURE_CACHE_TTL_SECONDS"), 60)),
        ControlsEnabled = ParseBool("CONTROLS_ENABLED", env("CONTROLS_ENABLED"), true),
        LogLevel = ParseLogLevel(env("LOG_LEVEL")),
        AzureDebug = ParseBool("AZURE_DEBUG", env("AZURE_DEBUG"), false),
        SpiffeJwtFile = Blank(env("SPIFFE_JWT_FILE")),
        ImdsEnabled = ParseBool("IMDS_ENABLED", env("IMDS_ENABLED"), true),
        ImdsApiVersion = Blank(env("IMDS_API_VERSION")) ?? "2021-02-01",
    };

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool? TryParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };

    static bool ParseBool(string name, string? value, bool fallback) =>
        Blank(value) is null ? fallback : TryParseBool(value) ?? throw Invalid(name, value, "true or false");

    static int ParseNonNegativeInt(string name, string? value, int fallback) =>
        Blank(value) is not { } v ? fallback
        : int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n
        : throw Invalid(name, value, "a non-negative integer");

    static AzureCloud ParseCloud(string? value) => Blank(value)?.ToLowerInvariant() switch
    {
        null or "public" => AzureCloud.Public,
        "usgov" => AzureCloud.UsGov,
        "china" => AzureCloud.China,
        _ => throw Invalid("AZURE_CLOUD", value, "public, usgov or china"),
    };

    static LogLevel ParseLogLevel(string? value) => Blank(value)?.ToLowerInvariant() switch
    {
        null or "information" or "info" => LogLevel.Information,
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        "none" => LogLevel.None,
        _ => throw Invalid("LOG_LEVEL", value, "Trace, Debug, Information, Warning, Error, Critical or None"),
    };

    static ArgumentException Invalid(string name, string? value, string expected) =>
        new($"{name} must be {expected}, got '{value}'");
}
