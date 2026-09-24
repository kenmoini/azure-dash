using AzureDash.Configuration;
using Microsoft.Extensions.Logging;

namespace AzureDash.Tests;

public class AppSettingsTests
{
    static EnvLookup Env(params (string Key, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return name => d.GetValueOrDefault(name);
    }

    [Fact]
    public void Defaults_when_environment_is_empty()
    {
        var s = AppSettings.FromEnvironment(Env());
        Assert.Equal(AuthMode.Auto, s.AuthMode);
        Assert.Null(s.SubscriptionId);
        Assert.Null(s.ResourceGroup);
        Assert.Equal(AzureCloud.Public, s.Cloud);
        Assert.Equal(TimeSpan.FromSeconds(60), s.CacheTtl);
        Assert.True(s.ControlsEnabled);
        Assert.Equal(LogLevel.Information, s.LogLevel);
        Assert.False(s.AzureDebug);
        Assert.Null(s.SpiffeJwtFile);
        Assert.True(s.ImdsEnabled);
        Assert.Equal("2021-02-01", s.ImdsApiVersion);
    }

    [Fact]
    public void Parses_every_variable()
    {
        var s = AppSettings.FromEnvironment(Env(
            ("AUTH_MODE", "spiffe"), ("AZURE_SUBSCRIPTION_ID", " sub-1 "), ("AZURE_RESOURCE_GROUP", "rg-1"),
            ("AZURE_CLOUD", "usgov"), ("AZURE_CACHE_TTL_SECONDS", "5"), ("CONTROLS_ENABLED", "off"),
            ("LOG_LEVEL", "DEBUG"), ("AZURE_DEBUG", "yes"), ("SPIFFE_JWT_FILE", "/run/token"),
            ("IMDS_ENABLED", "0"), ("IMDS_API_VERSION", "2025-04-07")));
        Assert.Equal(AuthMode.Spiffe, s.AuthMode);
        Assert.Equal("sub-1", s.SubscriptionId);
        Assert.Equal("rg-1", s.ResourceGroup);
        Assert.Equal(AzureCloud.UsGov, s.Cloud);
        Assert.Equal(TimeSpan.FromSeconds(5), s.CacheTtl);
        Assert.False(s.ControlsEnabled);
        Assert.Equal(LogLevel.Debug, s.LogLevel);
        Assert.True(s.AzureDebug);
        Assert.Equal("/run/token", s.SpiffeJwtFile);
        Assert.False(s.ImdsEnabled);
        Assert.Equal("2025-04-07", s.ImdsApiVersion);
    }

    [Fact]
    public void Blank_values_are_treated_as_unset()
    {
        var s = AppSettings.FromEnvironment(Env(("AZURE_SUBSCRIPTION_ID", "  "), ("AUTH_MODE", "")));
        Assert.Null(s.SubscriptionId);
        Assert.Equal(AuthMode.Auto, s.AuthMode);
    }

    [Theory]
    [InlineData("AUTH_MODE", "magic")]
    [InlineData("AZURE_CLOUD", "mars")]
    [InlineData("LOG_LEVEL", "loud")]
    [InlineData("AZURE_CACHE_TTL_SECONDS", "-1")]
    [InlineData("CONTROLS_ENABLED", "maybe")]
    public void Invalid_values_throw_naming_the_variable(string name, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => AppSettings.FromEnvironment(Env((name, value))));
        Assert.Contains(name, ex.Message);
    }

    [Theory]
    [InlineData("info", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("ERROR", LogLevel.Error)]
    public void Log_level_aliases(string value, LogLevel expected) =>
        Assert.Equal(expected, AppSettings.FromEnvironment(Env(("LOG_LEVEL", value))).LogLevel);
}
