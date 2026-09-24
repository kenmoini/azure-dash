# azure-dash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build azure-dash, a .NET 10 port of gcp-dash. It lists Azure resources through Entra Workload ID on AKS, and through federation from OpenShift (ZTWIM/SPIRE, or the cluster's own issuer). It also provides liveness, readiness, CPU and crash controls, and shows runtime, identity and IMDS information.

**Architecture:** One ASP.NET Core app (Razor Pages + minimal APIs). Pages are server-rendered, and htmx swaps in fragments.
- **Fragments:** static Razor components returned through `RazorComponentResult<T>`. The full pages embed the same components.
- **Azure data:** `IAzureProvider` (live implementation on Azure.ResourceManager) sits behind a TTL cache. The cache keeps the last good value when a fetch fails, so each panel fails on its own.
- **Credentials:** chosen explicitly by `AUTH_MODE`; the app never uses `DefaultAzureCredential`.
- **Deployment:** Kustomize base plus four overlays: AKS, OpenShift with a client secret, OpenShift with its native service-account issuer, and OpenShift ZTWIM.

**Tech Stack:**
- .NET 10 (SDK ≥ 10.0.100), ASP.NET Core Razor Pages and Razor components, htmx 2.0.11
- Azure.Identity 1.21.0, Azure.ResourceManager 1.14.0 (+ Compute 1.17.0, Network 1.17.0, Storage 1.7.0, ResourceGraph 1.1.1)
- xUnit 2.9.3 + Microsoft.AspNetCore.Mvc.Testing 10.0.12
- Kustomize, GitHub Actions, podman

**Spec:** `docs/superpowers/specs/2026-09-23-azure-dash-design.md`

## Global Constraints

- Target framework `net10.0`, with nullable enabled, implicit usings, and warnings treated as errors (except NuGet audit warnings NU1901–NU1904).
- Central package management: every version is declared only in `Directory.Packages.props`.
- `DefaultAzureCredential` is never used. The credential is chosen from `AUTH_MODE`: `auto|workload-identity|spiffe|client-secret|dev`.
  - In `auto`, the first match wins: `AZURE_FEDERATED_TOKEN_FILE` → workload-identity, `SPIFFE_JWT_FILE` → spiffe, `AZURE_CLIENT_SECRET` → client-secret, otherwise dev.
- The federated-token audience is always `api://AzureADTokenExchange`.
- The ARM scope comes from `AZURE_CLOUD` (`public|usgov|china`):
  - public: `https://management.azure.com/.default`
  - usgov: `https://management.usgovcloudapi.net/.default`
  - china: `https://management.chinacloudapi.cn/.default`
- Never hard-code the token path; read `AZURE_FEDERATED_TOKEN_FILE`.
- Raw tokens are never rendered or logged. Only decoded, selected claims are shown.
- IMDS: `http://169.254.169.254/metadata/instance/compute`, header `Metadata: true`, no proxy, 1 s timeout, default `IMDS_API_VERSION=2021-02-01`.
- JSON APIs use snake_case property names (`JsonNamingPolicy.SnakeCaseLower`), matching gcp-dash.
- Any endpoint that serves a fragment returns HTML when the request carries `HX-Request: true`, and JSON otherwise.
- The container listens on port 8080 (`ASPNETCORE_HTTP_PORTS`) and runs as non-root UID 1654 (`$APP_UID`).
- Image: `ghcr.io/kenmoini/azure-dash`.
- Kubernetes names:
  - namespace `azure-dash` (ZTWIM overlay: `azure-dash-ztwim`)
  - ServiceAccount `azure-dash`, Deployment `azure-dash`, Service `azure-dash` on port 8080
- Shell prerequisite: every command that runs `dotnet` is preceded (once per shell) by `export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1`. Task 1 installs the SDK there.

**Deliberate deviations from the spec, decided while planning:**
- The C# namespace and folder for resource inventory is `AzureDash.Inventory` (`src/AzureDash/Inventory/`), not `Azure/`. A namespace `AzureDash.Azure` would shadow the Azure SDK's root `Azure` namespace.
- The `AuthMode` and `AzureCloud` enums live in `Configuration/`.
- VM private and public IPs are omitted. They need one NIC lookup per VM, which isn't "cheap".
- NSG panels show custom rules only; the default rules are left out.
- CI does a single multi-platform `docker/build-push-action` build, not gcp-dash's matrix-plus-merge. The SDK stage cross-compiles on `$BUILDPLATFORM` and the final stage has no `RUN`, so no QEMU is needed.
- Log redaction relies on Azure.Core, which already redacts `Authorization` and other non-allow-listed headers, plus the app never logging token values. No custom redacting formatter is ported.

## Review Focus

1. **SPIFFE token file missing, empty, base64-wrapped, or ending in a newline** (the sidecar hasn't written it yet, or Red Hat's "base64-encoded" wording is literal). Expect a clear error that names the file and spiffe-helper, or a successful read. The file is re-read on every exchange. → Task 7 tests.
2. **Reader scoped only to a resource group, with `AZURE_SUBSCRIPTION_ID` unset.** No subscriptions are visible, so expect an explicit "set AZURE_SUBSCRIPTION_ID" message rather than an empty page. → Task 8 `ScopeResolverTests`.
3. **IMDS unreachable, hanging, returning 4xx, or returning non-JSON** (not on Azure, or `--enable-imds-restriction`). Expect "unavailable: reason" within about 1 s, never a hang or a 500. → Task 9 `ImdsClientTests`.
4. **Federated token file holds garbage or isn't a JWT.** The Identity panel should show a decode error and still attempt the Entra token, never a 500. → Task 9 `IdentityInfoServiceTests`.
5. **Bad control input** (`workers=abc`, `workers=0`, `exit_code=256`, missing `enabled`). Expect a 422 with a message, never a 500 or a crash. → Task 2 `ControlsTests`.

---

## File map

```
azure-dash.slnx  global.json  Directory.Build.props  Directory.Packages.props  .gitignore  .containerignore
Containerfile  README.md  scripts/run-podman.sh  .github/workflows/build-container.yaml  .github/dependabot.yml
src/AzureDash/
  AzureDash.csproj  Program.cs  AppInfo.cs  SelfProbe.cs  AzureSdkLogging.cs
  Configuration/  EnvLookup.cs AuthMode.cs AzureCloud.cs AppSettings.cs
  State/          RuntimeState.cs StateService.cs IProcessExit.cs
  Load/           CpuLoad.cs
  Runtime/        RuntimeInfo.cs RuntimeInfoProvider.cs
  Inventory/      Models.cs AzureKinds.cs IAzureProvider.cs AzureError.cs TtlCache.cs AzureService.cs
                  ScopeResolver.cs AzureMappers.cs LiveAzureProvider.cs
  Identity/       CloudEndpoints.cs CredentialFactory.cs CredentialProvider.cs SpiffeAssertionSource.cs
                  JwtDisplay.cs IdentityInfoService.cs
  Imds/           ImdsClient.cs
  Endpoints/      ErrorBody.cs Htmx.cs Input.cs HealthEndpoints.cs ControlsEndpoints.cs RuntimeEndpoints.cs
                  AzureEndpoints.cs IdentityEndpoints.cs
  Pages/          _ViewImports.cshtml _ViewStart.cshtml Shared/_Layout.cshtml Index.cshtml Azure.cshtml Identity.cshtml
  Components/     _Imports.razor Fmt.cs
    Partials/     RuntimePartial.razor ControlsPartial.razor AzurePanel.razor IdentityPartial.razor
                  ImdsPartial.razor ClaimsTable.razor
    Partials/Inventory/ SubscriptionTable.razor ResourceGroupsTable.razor VmsTable.razor StorageAccountsTable.razor
                  VnetsTable.razor SubnetsTable.razor NsgRulesTable.razor ResourceGraphTable.razor
  wwwroot/css/site.css  wwwroot/js/htmx.min.js
tests/AzureDash.Tests/
  AzureDash.Tests.csproj
  Support/  AppFactory.cs ManualTimeProvider.cs HttpExtensions.cs FakeProcessExit.cs FakeAzureProvider.cs
            FakeRoot.cs TestJwt.cs FakeTokenCredential.cs StubHandler.cs
  *Tests.cs (one per unit, named in each task)
deploy/README.md
deploy/kubernetes/  kustomization.yaml namespace.yaml serviceaccount.yaml configmap.yaml deployment.yaml service.yaml
deploy/aks/         kustomization.yaml ingress.yaml README.md
deploy/openshift/   kustomization.yaml route.yaml secret.example.yaml README.md
deploy/openshift-wi/ kustomization.yaml route.yaml identity-configmap.yaml README.md
deploy/ztwim/       kustomization.yaml route.yaml identity-configmap.yaml spiffe-helper-configmap.yaml README.md
```

---

### Task 1: Toolchain, solution scaffold, settings, health probes

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `azure-dash.slnx`
- Create: `src/AzureDash/AzureDash.csproj`, `src/AzureDash/Program.cs`
- Create: `src/AzureDash/Configuration/{EnvLookup,AuthMode,AzureCloud,AppSettings}.cs`
- Create: `src/AzureDash/State/RuntimeState.cs`, `src/AzureDash/Endpoints/{HealthEndpoints,ErrorBody}.cs`
- Create: `tests/AzureDash.Tests/AzureDash.Tests.csproj`, `tests/AzureDash.Tests/Support/{AppFactory,ManualTimeProvider}.cs`
- Test: `tests/AzureDash.Tests/AppSettingsTests.cs`, `tests/AzureDash.Tests/HealthTests.cs`

**Interfaces:**
- Produces:
  - `delegate string? EnvLookup(string name)`
  - `enum AuthMode { Auto, WorkloadIdentity, Spiffe, ClientSecret, Dev }`
  - `AuthModes.Name(AuthMode) → string`, `AuthModes.Parse(string?) → AuthMode?`
  - `enum AzureCloud { Public, UsGov, China }`
  - `record AppSettings` with `AuthMode`, `SubscriptionId?`, `ResourceGroup?`, `Cloud`, `CacheTtl`, `ControlsEnabled`, `LogLevel`, `AzureDebug`, `SpiffeJwtFile?`, `ImdsEnabled`, `ImdsApiVersion`
    - `AppSettings.FromEnvironment(EnvLookup)`
    - `internal static string? Blank(string?)`, `internal static bool? TryParseBool(string?)`
  - `RuntimeState(TimeProvider)` with `Live`, `Ready`, `StartedAt`
  - `record ErrorBody(string Detail)`
  - `HealthEndpoints.MapHealthEndpoints(IEndpointRouteBuilder)`
  - Test `AppFactory : WebApplicationFactory<Program>` with `Settings` (init) and `Time`

- [ ] **Step 1: Install the .NET 10 SDK locally (no sudo)**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet --version
```
Expected: `10.0.x`, where x ≥ 100.

- [ ] **Step 2: Write the repo scaffold files**

`global.json`:
```json
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Azure.Identity" Version="1.21.0" />
    <PackageVersion Include="Azure.ResourceManager" Version="1.14.0" />
    <PackageVersion Include="Azure.ResourceManager.Compute" Version="1.17.0" />
    <PackageVersion Include="Azure.ResourceManager.Network" Version="1.17.0" />
    <PackageVersion Include="Azure.ResourceManager.Storage" Version="1.7.0" />
    <PackageVersion Include="Azure.ResourceManager.ResourceGraph" Version="1.1.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
```

`.gitignore`:
```
bin/
obj/
.vs/
.idea/
*.user
TestResults/
.DS_Store
deploy/**/secret.yaml
.claude/
.superpowers/
```

`src/AzureDash/AzureDash.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>AzureDash</RootNamespace>
    <AssemblyName>AzureDash</AssemblyName>
    <Version>0.1.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="AzureDash.Tests" />
  </ItemGroup>
</Project>
```

`tests/AzureDash.Tests/AzureDash.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\AzureDash\AzureDash.csproj" />
  </ItemGroup>
</Project>
```

Create the solution:
```bash
dotnet new sln -n azure-dash --format slnx
dotnet sln azure-dash.slnx add src/AzureDash/AzureDash.csproj tests/AzureDash.Tests/AzureDash.Tests.csproj
```

- [ ] **Step 3: Write the configuration types**

`src/AzureDash/Configuration/EnvLookup.cs`:
```csharp
namespace AzureDash.Configuration;

/// <summary>Reads an environment variable; injectable so tests can supply a fake environment.</summary>
public delegate string? EnvLookup(string name);
```

`src/AzureDash/Configuration/AuthMode.cs`:
```csharp
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
```

`src/AzureDash/Configuration/AzureCloud.cs`:
```csharp
namespace AzureDash.Configuration;

public enum AzureCloud { Public, UsGov, China }
```

- [ ] **Step 4: Write the failing settings tests**

`tests/AzureDash.Tests/AppSettingsTests.cs`:
```csharp
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
```

- [ ] **Step 5: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~AppSettingsTests"`
Expected: a build error, because `AppSettings` doesn't exist yet.

- [ ] **Step 6: Implement `AppSettings`**

`src/AzureDash/Configuration/AppSettings.cs`:
```csharp
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
```

- [ ] **Step 7: Run the settings tests and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~AppSettingsTests"`
Expected: PASS (5 tests plus the theory cases).

- [ ] **Step 8: Write the test support classes and the failing health tests**

`tests/AzureDash.Tests/Support/ManualTimeProvider.cs`:
```csharp
namespace AzureDash.Tests.Support;

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
```

`tests/AzureDash.Tests/Support/AppFactory.cs`:
```csharp
using AzureDash.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureDash.Tests.Support;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public AppSettings Settings { get; init; } = new() { ImdsEnabled = false };
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppSettings>();
            services.AddSingleton(Settings);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }
}
```

`tests/AzureDash.Tests/HealthTests.cs`:
```csharp
using System.Net;
using AzureDash.State;
using AzureDash.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AzureDash.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    public async Task Probes_are_healthy_by_default(string path)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_returns_503_when_disabled()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        f.Services.GetRequiredService<RuntimeState>().Live = false;
        var r = await client.GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("{\"status\":\"failing\",\"reason\":\"liveness disabled via controls\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_returns_503_when_disabled()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        f.Services.GetRequiredService<RuntimeState>().Ready = false;
        var r = await client.GetAsync("/healthz/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("readiness disabled via controls", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Started_at_comes_from_the_time_provider()
    {
        using var f = new AppFactory();
        _ = f.CreateClient();
        Assert.Equal(f.Time.Now, f.Services.GetRequiredService<RuntimeState>().StartedAt);
    }
}
```

- [ ] **Step 9: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~HealthTests"`
Expected: a build error, because `Program`, `RuntimeState` and the endpoints don't exist yet.

- [ ] **Step 10: Implement the state, health endpoints and `Program`**

`src/AzureDash/State/RuntimeState.cs`:
```csharp
namespace AzureDash.State;

/// <summary>Probe flags flipped by the controls; in-memory only, so a restart resets them to healthy.</summary>
public sealed class RuntimeState(TimeProvider time)
{
    private readonly object _gate = new();
    private bool _live = true;
    private bool _ready = true;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public bool Live
    {
        get { lock (_gate) return _live; }
        set { lock (_gate) _live = value; }
    }

    public bool Ready
    {
        get { lock (_gate) return _ready; }
        set { lock (_gate) _ready = value; }
    }
}
```

`src/AzureDash/Endpoints/ErrorBody.cs`:
```csharp
namespace AzureDash.Endpoints;

public sealed record ErrorBody(string Detail);
```

`src/AzureDash/Endpoints/HealthEndpoints.cs`:
```csharp
using System.Text.Json.Serialization;
using AzureDash.State;

namespace AzureDash.Endpoints;

public sealed record ProbeStatus(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz/live", (RuntimeState s) => Probe(s.Live, "liveness"));
        app.MapGet("/healthz/ready", (RuntimeState s) => Probe(s.Ready, "readiness"));
        return app;
    }

    static IResult Probe(bool ok, string what) => ok
        ? Results.Json(new ProbeStatus("ok", null))
        : Results.Json(new ProbeStatus("failing", $"{what} disabled via controls"), statusCode: 503);
}
```

`src/AzureDash/Program.cs`:
```csharp
using System.Text.Json;
using AzureDash.Configuration;
using AzureDash.Endpoints;
using AzureDash.State;

EnvLookup env = Environment.GetEnvironmentVariable;
var settings = AppSettings.FromEnvironment(env);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(settings.LogLevel);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(env);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
```

- [ ] **Step 11: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS (every test in AppSettingsTests and HealthTests).

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: scaffold azure-dash solution with settings and health probes"
```

---
### Task 2: CPU load and controls API

**Files:**
- Create: `src/AzureDash/Load/CpuLoad.cs`, `src/AzureDash/State/{StateService,IProcessExit}.cs`
- Create: `src/AzureDash/Endpoints/{Htmx,Input,ControlsEndpoints}.cs`
- Modify: `src/AzureDash/Program.cs` (full replacement below)
- Create: `tests/AzureDash.Tests/Support/{HttpExtensions,FakeProcessExit}.cs`; Modify: `tests/AzureDash.Tests/Support/AppFactory.cs` (full replacement below)
- Test: `tests/AzureDash.Tests/CpuLoadTests.cs`, `tests/AzureDash.Tests/ControlsTests.cs`

**Interfaces:**
- Consumes: `RuntimeState`, `AppSettings`, `ErrorBody` (Task 1).
- Produces:
  - `record CpuLoadStatus(bool Active, int Workers)`
  - `CpuLoad(int? maxWorkers = null)` with `Status`, `Start(int) → CpuLoadStatus`, `Stop() → CpuLoadStatus`, `IDisposable`
  - `record StateSnapshot(bool Live, bool Ready, DateTimeOffset StartedAt, double UptimeSeconds, bool ControlsEnabled, CpuLoadStatus CpuLoad)`
  - `StateService.Snapshot()`
  - `IProcessExit.Exit(int)`
  - `Htmx.IsHtmx(HttpRequest)`
  - `ControlsEndpoints.MapControlsEndpoints`. It has a private `Respond(HttpContext, StateService)` that returns JSON; Task 4 changes it to return a fragment for htmx requests.
  - Test helpers: `HttpExtensions.JsonAsync / PostFormAsync / HxPostFormAsync / HxGetAsync`, and `AppFactory.Exit` (a `FakeProcessExit`)

- [ ] **Step 1: Write the failing CPU load tests**

`tests/AzureDash.Tests/CpuLoadTests.cs`:
```csharp
using AzureDash.Load;

namespace AzureDash.Tests;

public class CpuLoadTests
{
    [Fact]
    public void Starts_and_stops_workers()
    {
        using var cpu = new CpuLoad(maxWorkers: 4);
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Status);
        Assert.Equal(new CpuLoadStatus(true, 2), cpu.Start(2));
        Assert.Equal(new CpuLoadStatus(true, 2), cpu.Status);
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Stop());
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Status);
    }

    [Fact]
    public void Clamps_workers_to_the_maximum()
    {
        using var cpu = new CpuLoad(maxWorkers: 2);
        Assert.Equal(2, cpu.Start(64).Workers);
    }

    [Fact]
    public void Start_is_idempotent_while_running()
    {
        using var cpu = new CpuLoad(maxWorkers: 4);
        cpu.Start(1);
        Assert.Equal(1, cpu.Start(3).Workers);
    }

    [Fact]
    public void Stop_when_idle_is_a_no_op()
    {
        using var cpu = new CpuLoad(maxWorkers: 1);
        Assert.False(cpu.Stop().Active);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~CpuLoadTests"`
Expected: a build error, because `CpuLoad` doesn't exist yet.

- [ ] **Step 3: Implement `CpuLoad`**

`src/AzureDash/Load/CpuLoad.cs`:
```csharp
namespace AzureDash.Load;

public sealed record CpuLoadStatus(bool Active, int Workers);

/// <summary>Burns CPU on N background threads (no GIL in .NET, so threads saturate cores).</summary>
public sealed class CpuLoad : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maxWorkers;
    private readonly List<Thread> _threads = [];
    private CancellationTokenSource? _cts;

    public CpuLoad(int? maxWorkers = null) => _maxWorkers = Math.Max(1, maxWorkers ?? Environment.ProcessorCount);

    public CpuLoadStatus Status
    {
        get { lock (_gate) return new(_cts is not null, _threads.Count); }
    }

    public CpuLoadStatus Start(int workers)
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var count = Math.Clamp(workers, 1, _maxWorkers);
                for (var i = 0; i < count; i++)
                {
                    var thread = new Thread(() => Spin(token)) { IsBackground = true, Name = $"cpu-load-{i}" };
                    thread.Start();
                    _threads.Add(thread);
                }
            }
            return new(true, _threads.Count);
        }
    }

    public CpuLoadStatus Stop()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                _cts.Cancel();
                foreach (var thread in _threads) thread.Join(TimeSpan.FromSeconds(2));
                _threads.Clear();
                _cts.Dispose();
                _cts = null;
            }
            return new(false, 0);
        }
    }

    public void Dispose() => Stop();

    private static void Spin(CancellationToken token)
    {
        while (!token.IsCancellationRequested) Thread.SpinWait(10_000);
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~CpuLoadTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Write the test support classes and the failing controls tests**

`tests/AzureDash.Tests/Support/HttpExtensions.cs`:
```csharp
using System.Text.Json;

namespace AzureDash.Tests.Support;

public static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, params (string Key, string Value)[] fields) =>
        client.SendAsync(Form(url, htmx: false, fields));

    public static Task<HttpResponseMessage> HxPostFormAsync(this HttpClient client, string url, params (string Key, string Value)[] fields) =>
        client.SendAsync(Form(url, htmx: true, fields));

    public static Task<HttpResponseMessage> HxGetAsync(this HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return client.SendAsync(request);
    }

    static HttpRequestMessage Form(string url, bool htmx, (string Key, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        if (htmx) request.Headers.Add("HX-Request", "true");
        return request;
    }
}
```

`tests/AzureDash.Tests/Support/FakeProcessExit.cs`:
```csharp
using AzureDash.State;

namespace AzureDash.Tests.Support;

public sealed class FakeProcessExit : IProcessExit
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<int> Exited => _exited.Task;
    public void Exit(int code) => _exited.TrySetResult(code);
}
```

Replace `tests/AzureDash.Tests/Support/AppFactory.cs` with:
```csharp
using AzureDash.Configuration;
using AzureDash.Load;
using AzureDash.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureDash.Tests.Support;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public AppSettings Settings { get; init; } = new() { ImdsEnabled = false };
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public FakeProcessExit Exit { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppSettings>();
            services.AddSingleton(Settings);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IProcessExit>();
            services.AddSingleton<IProcessExit>(Exit);
            services.RemoveAll<CpuLoad>();
            services.AddSingleton(_ => new CpuLoad(maxWorkers: 2));
        });
    }
}
```

`tests/AzureDash.Tests/ControlsTests.cs`:
```csharp
using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class ControlsTests
{
    [Fact]
    public async Task State_uses_snake_case()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/state")).JsonAsync();
        Assert.True(json.GetProperty("live").GetBoolean());
        Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.True(json.GetProperty("controls_enabled").GetBoolean());
        Assert.Equal(0, json.GetProperty("uptime_seconds").GetDouble());
        Assert.False(json.GetProperty("cpu_load").GetProperty("active").GetBoolean());
    }

    [Theory]
    [InlineData("/controls/readiness", "/healthz/ready", "ready")]
    [InlineData("/controls/liveness", "/healthz/live", "live")]
    public async Task Toggle_flips_probe(string control, string probe, string key)
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        var r = await client.PostFormAsync(control, ("enabled", "false"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await r.JsonAsync()).GetProperty(key).GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(probe)).StatusCode);

        await client.PostFormAsync(control, ("enabled", "true"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(probe)).StatusCode);
    }

    [Theory]
    [InlineData("/controls/readiness")]
    [InlineData("/controls/cpu")]
    public async Task Missing_or_invalid_enabled_is_422(string control)
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostFormAsync(control)).StatusCode);
        var r = await client.PostFormAsync(control, ("enabled", "perhaps"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("enabled", (await r.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Cpu_start_clamps_and_stop()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        var on = await (await client.PostFormAsync("/controls/cpu", ("enabled", "true"), ("workers", "5"))).JsonAsync();
        Assert.True(on.GetProperty("cpu_load").GetProperty("active").GetBoolean());
        Assert.Equal(2, on.GetProperty("cpu_load").GetProperty("workers").GetInt32());
        var off = await (await client.PostFormAsync("/controls/cpu", ("enabled", "false"))).JsonAsync();
        Assert.False(off.GetProperty("cpu_load").GetProperty("active").GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("abc")]
    public async Task Cpu_invalid_workers_is_422(string workers)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/cpu", ("enabled", "true"), ("workers", workers));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("workers", (await r.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Crash_returns_202_then_exits_with_code()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/crash", ("exit_code", "7"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(7, (await r.JsonAsync()).GetProperty("exit_code").GetInt32());
        Assert.Equal(7, await f.Exit.Exited.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("x")]
    public async Task Crash_invalid_exit_code_is_422_and_does_not_exit(string code)
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().PostFormAsync("/controls/crash", ("exit_code", code));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        await Task.Delay(700);
        Assert.False(f.Exit.Exited.IsCompleted);
    }

    [Fact]
    public async Task Controls_disabled_returns_403_but_state_still_works()
    {
        using var f = new AppFactory { Settings = new() { ControlsEnabled = false, ImdsEnabled = false } };
        var client = f.CreateClient();
        var r = await client.PostFormAsync("/controls/liveness", ("enabled", "false"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("controls are disabled (CONTROLS_ENABLED=false)", (await r.JsonAsync()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz/live")).StatusCode);
        Assert.False((await (await client.GetAsync("/api/state")).JsonAsync()).GetProperty("controls_enabled").GetBoolean());
    }
}
```

- [ ] **Step 6: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~ControlsTests"`
Expected: a build error, because `IProcessExit` and the endpoints don't exist yet.

- [ ] **Step 7: Implement the state service, the exit abstraction and the controls endpoints**

`src/AzureDash/State/IProcessExit.cs`:
```csharp
namespace AzureDash.State;

public interface IProcessExit
{
    void Exit(int code);
}

public sealed class EnvironmentProcessExit : IProcessExit
{
    public void Exit(int code) => Environment.Exit(code);
}
```

`src/AzureDash/State/StateService.cs`:
```csharp
using AzureDash.Configuration;
using AzureDash.Load;

namespace AzureDash.State;

public sealed record StateSnapshot(
    bool Live, bool Ready, DateTimeOffset StartedAt, double UptimeSeconds, bool ControlsEnabled, CpuLoadStatus CpuLoad);

public sealed class StateService(RuntimeState state, CpuLoad cpu, AppSettings settings, TimeProvider time)
{
    public StateSnapshot Snapshot() => new(
        state.Live,
        state.Ready,
        state.StartedAt,
        Math.Round((time.GetUtcNow() - state.StartedAt).TotalSeconds, 1),
        settings.ControlsEnabled,
        cpu.Status);
}
```

`src/AzureDash/Endpoints/Htmx.cs`:
```csharp
namespace AzureDash.Endpoints;

public static class Htmx
{
    public static bool IsHtmx(HttpRequest request) => request.Headers["HX-Request"] == "true";
}
```

`src/AzureDash/Endpoints/Input.cs`:
```csharp
using System.Globalization;
using AzureDash.Configuration;

namespace AzureDash.Endpoints;

/// <summary>Reads form + query values without [FromForm] binding (which would demand antiforgery tokens).</summary>
internal static class Input
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(HttpRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Query) values[key] = value.ToString();
        if (request.HasFormContentType)
            foreach (var (key, value) in await request.ReadFormAsync()) values[key] = value.ToString();
        return values;
    }

    public static bool? Bool(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? AppSettings.TryParseBool(v) : null;

    /// <summary>Returns <paramref name="fallback"/> when absent/blank, the value when in range, otherwise null.</summary>
    public static int? Int(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : null;
    }
}
```

`src/AzureDash/Endpoints/ControlsEndpoints.cs`:
```csharp
using AzureDash.Configuration;
using AzureDash.Load;
using AzureDash.State;

namespace AzureDash.Endpoints;

public sealed record CrashResponse(string Message, int ExitCode);

public static class ControlsEndpoints
{
    public static IEndpointRouteBuilder MapControlsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/state", (StateService s) => Results.Json(s.Snapshot()));

        var controls = app.MapGroup("/controls").AddEndpointFilter(RequireControlsEnabled);
        controls.MapPost("/liveness", (HttpContext ctx, RuntimeState st, StateService s) => Toggle(ctx, s, v => st.Live = v));
        controls.MapPost("/readiness", (HttpContext ctx, RuntimeState st, StateService s) => Toggle(ctx, s, v => st.Ready = v));
        controls.MapPost("/cpu", Cpu);
        controls.MapPost("/crash", Crash);
        return app;
    }

    static async Task<IResult> Toggle(HttpContext ctx, StateService s, Action<bool> apply)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Bool(form, "enabled") is not bool enabled) return Unprocessable("'enabled' must be true or false");
        apply(enabled);
        return Respond(ctx, s);
    }

    static async Task<IResult> Cpu(HttpContext ctx, CpuLoad cpu, StateService s)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Bool(form, "enabled") is not bool enabled) return Unprocessable("'enabled' must be true or false");
        if (Input.Int(form, "workers", 1, 1, 64) is not int workers) return Unprocessable("'workers' must be an integer between 1 and 64");
        if (enabled) cpu.Start(workers); else cpu.Stop();
        return Respond(ctx, s);
    }

    static async Task<IResult> Crash(HttpContext ctx, CpuLoad cpu, IProcessExit exit)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Int(form, "exit_code", 1, 0, 255) is not int code) return Unprocessable("'exit_code' must be an integer between 0 and 255");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            cpu.Stop();
            exit.Exit(code);
        });
        return Htmx.IsHtmx(ctx.Request)
            ? Results.Content($"<span class=\"badge bad\">Exiting with code {code}…</span>", "text/html", statusCode: 202)
            : Results.Json(new CrashResponse($"exiting with code {code}", code), statusCode: 202);
    }

    static IResult Respond(HttpContext ctx, StateService s) => Results.Json(s.Snapshot());

    static IResult Unprocessable(string detail) => Results.Json(new ErrorBody(detail), statusCode: 422);

    static async ValueTask<object?> RequireControlsEnabled(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (ctx.HttpContext.RequestServices.GetRequiredService<AppSettings>().ControlsEnabled) return await next(ctx);
        return Results.Json(new ErrorBody("controls are disabled (CONTROLS_ENABLED=false)"), statusCode: 403);
    }
}
```

Replace `src/AzureDash/Program.cs` with:
```csharp
using System.Text.Json;
using AzureDash.Configuration;
using AzureDash.Endpoints;
using AzureDash.Load;
using AzureDash.State;

EnvLookup env = Environment.GetEnvironmentVariable;
var settings = AppSettings.FromEnvironment(env);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(settings.LogLevel);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(env);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton(_ => new CpuLoad());
builder.Services.AddSingleton<StateService>();
builder.Services.AddSingleton<IProcessExit, EnvironmentProcessExit>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.MapHealthEndpoints();
app.MapControlsEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
```

- [ ] **Step 8: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS (every test so far).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add CPU load, crash, and probe toggle controls"
```

---

### Task 3: Runtime info (container, Kubernetes, cgroups)

**Files:**
- Create: `src/AzureDash/Runtime/{RuntimeInfo,RuntimeInfoProvider}.cs`, `src/AzureDash/Endpoints/RuntimeEndpoints.cs`
- Modify: `src/AzureDash/Program.cs`
- Create: `tests/AzureDash.Tests/Support/FakeRoot.cs`
- Test: `tests/AzureDash.Tests/RuntimeInfoTests.cs`

**Interfaces:**
- Consumes: `EnvLookup`, `AppSettings.Blank`, `StateService`, `StateSnapshot` (Tasks 1–2).
- Produces:
  - `record PodInfo(string? Name, string? Namespace, string? Node, string? Ip, string? ServiceAccount)`
  - `record CgroupInfo(string Version, double? CpuLimitCores, long? MemoryLimitBytes, long? MemoryUsageBytes)`, where `Version` is `"v2"`, `"v1"` or `"none"`
  - `record RuntimeInfo(string Hostname, string ContainerRuntime, string Orchestrator, PodInfo? Pod, CgroupInfo Cgroup, string Os, string Dotnet, string Gc, int ProcessorCount, int? Uid, int? Gid, int Pid, double? LoadAverage1m, IReadOnlyDictionary<string,string?> EnvHints, StateSnapshot State)`
  - `RuntimeInfoProvider(string root, EnvLookup env, StateService state).Get()`
  - `RuntimeEndpoints.MapRuntimeEndpoints`: `GET /api/runtime` in this task; Task 4 adds `/partials/runtime`

- [ ] **Step 1: Write the test support class and the failing tests**

`tests/AzureDash.Tests/Support/FakeRoot.cs`:
```csharp
namespace AzureDash.Tests.Support;

/// <summary>A temporary directory standing in for "/" so /proc, /sys and /etc can be faked.</summary>
public sealed class FakeRoot : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("azure-dash-root-").FullName;

    public FakeRoot Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public FakeRoot Dir(string relative)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));
        return this;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
```

`tests/AzureDash.Tests/RuntimeInfoTests.cs`:
```csharp
using AzureDash.Configuration;
using AzureDash.Load;
using AzureDash.Runtime;
using AzureDash.State;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class RuntimeInfoTests
{
    static RuntimeInfo Get(FakeRoot root, params (string Key, string Value)[] env)
    {
        var d = env.ToDictionary(e => e.Key, e => e.Value);
        var state = new StateService(new RuntimeState(TimeProvider.System), new CpuLoad(1), new AppSettings(), TimeProvider.System);
        return new RuntimeInfoProvider(root.Path, name => d.GetValueOrDefault(name), state).Get();
    }

    [Fact]
    public void Detects_podman() { using var r = new FakeRoot().Write("run/.containerenv", ""); Assert.Equal("podman", Get(r).ContainerRuntime); }

    [Fact]
    public void Detects_docker() { using var r = new FakeRoot().Write(".dockerenv", ""); Assert.Equal("docker", Get(r).ContainerRuntime); }

    [Fact]
    public void Uses_container_env_var() { using var r = new FakeRoot(); Assert.Equal("oci", Get(r, ("container", "oci")).ContainerRuntime); }

    [Theory]
    [InlineData("0::/kubepods.slice/crio-abc.scope", "cri-o")]
    [InlineData("0::/kubepods/besteffort/containerd-abc", "containerd")]
    [InlineData("0::/kubepods/pod123", "kubernetes")]
    [InlineData("0::/", "none")]
    public void Detects_runtime_from_cgroup_path(string cgroup, string expected)
    {
        using var r = new FakeRoot().Write("proc/self/cgroup", cgroup);
        Assert.Equal(expected, Get(r).ContainerRuntime);
    }

    [Fact]
    public void Kubernetes_pod_fields_from_downward_api()
    {
        using var r = new FakeRoot();
        var info = Get(r, ("KUBERNETES_SERVICE_HOST", "10.0.0.1"), ("POD_NAME", "azure-dash-abc"), ("POD_NAMESPACE", "azure-dash"),
            ("NODE_NAME", "aks-nodepool1-0"), ("POD_IP", "10.244.0.5"), ("SERVICE_ACCOUNT", "azure-dash"));
        Assert.Equal("kubernetes", info.Orchestrator);
        Assert.Equal(new PodInfo("azure-dash-abc", "azure-dash", "aks-nodepool1-0", "10.244.0.5", "azure-dash"), info.Pod);
        Assert.Equal("10.0.0.1", info.EnvHints["KUBERNETES_SERVICE_HOST"]);
    }

    [Fact]
    public void Namespace_falls_back_to_service_account_file()
    {
        using var r = new FakeRoot().Write("var/run/secrets/kubernetes.io/serviceaccount/namespace", "from-file\n");
        var info = Get(r);
        Assert.Equal("kubernetes", info.Orchestrator);
        Assert.Equal("from-file", info.Pod!.Namespace);
    }

    [Fact]
    public void No_orchestrator_means_no_pod()
    {
        using var r = new FakeRoot();
        var info = Get(r);
        Assert.Equal("none", info.Orchestrator);
        Assert.Null(info.Pod);
    }

    [Fact]
    public void Cgroup_v2_limits()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cgroup.controllers", "cpu memory")
            .Write("sys/fs/cgroup/cpu.max", "50000 100000\n").Write("sys/fs/cgroup/memory.max", "536870912\n")
            .Write("sys/fs/cgroup/memory.current", "1048576\n");
        Assert.Equal(new CgroupInfo("v2", 0.5, 536870912, 1048576), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v2_unlimited()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cgroup.controllers", "")
            .Write("sys/fs/cgroup/cpu.max", "max 100000").Write("sys/fs/cgroup/memory.max", "max");
        Assert.Equal(new CgroupInfo("v2", null, null, null), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v1_limits_and_huge_limit_means_unlimited()
    {
        using var r = new FakeRoot()
            .Write("sys/fs/cgroup/cpu/cpu.cfs_quota_us", "200000").Write("sys/fs/cgroup/cpu/cpu.cfs_period_us", "100000")
            .Write("sys/fs/cgroup/memory/memory.limit_in_bytes", "9223372036854771712")
            .Write("sys/fs/cgroup/memory/memory.usage_in_bytes", "4096");
        Assert.Equal(new CgroupInfo("v1", 2, null, 4096), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v1_negative_quota_is_unlimited()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cpu/cpu.cfs_quota_us", "-1").Write("sys/fs/cgroup/cpu/cpu.cfs_period_us", "100000");
        Assert.Null(Get(r).Cgroup.CpuLimitCores);
    }

    [Fact]
    public void No_cgroup_fs()
    {
        using var r = new FakeRoot();
        Assert.Equal(new CgroupInfo("none", null, null, null), Get(r).Cgroup);
    }

    [Fact]
    public void Os_uid_gid_and_load_average()
    {
        using var r = new FakeRoot()
            .Write("etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\n")
            .Write("proc/self/status", "Name:\tdotnet\nUid:\t1654\t1654\t1654\t1654\nGid:\t1654\t1654\t1654\t1654\n")
            .Write("proc/loadavg", "0.42 0.30 0.20 1/100 12345\n");
        var info = Get(r);
        Assert.Equal("Ubuntu 24.04.3 LTS", info.Os);
        Assert.Equal(1654, info.Uid);
        Assert.Equal(1654, info.Gid);
        Assert.Equal(0.42, info.LoadAverage1m);
        Assert.StartsWith(".NET", info.Dotnet);
        Assert.Equal(Environment.ProcessId, info.Pid);
    }

    [Fact]
    public async Task Api_runtime_includes_state()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/runtime")).JsonAsync();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("hostname").GetString()));
        Assert.True(json.GetProperty("state").GetProperty("live").GetBoolean());
        Assert.True(json.TryGetProperty("cgroup", out _));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~RuntimeInfoTests"`
Expected: a build error, because `RuntimeInfoProvider` doesn't exist yet.

- [ ] **Step 3: Implement the runtime info types, the provider and the endpoint**

`src/AzureDash/Runtime/RuntimeInfo.cs`:
```csharp
using AzureDash.State;

namespace AzureDash.Runtime;

public sealed record PodInfo(string? Name, string? Namespace, string? Node, string? Ip, string? ServiceAccount);

public sealed record CgroupInfo(string Version, double? CpuLimitCores, long? MemoryLimitBytes, long? MemoryUsageBytes);

public sealed record RuntimeInfo(
    string Hostname,
    string ContainerRuntime,
    string Orchestrator,
    PodInfo? Pod,
    CgroupInfo Cgroup,
    string Os,
    string Dotnet,
    string Gc,
    int ProcessorCount,
    int? Uid,
    int? Gid,
    int Pid,
    double? LoadAverage1m,
    IReadOnlyDictionary<string, string?> EnvHints,
    StateSnapshot State);
```

`src/AzureDash/Runtime/RuntimeInfoProvider.cs`:
```csharp
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using AzureDash.Configuration;
using AzureDash.State;

namespace AzureDash.Runtime;

/// <summary>Container/Kubernetes/cgroup introspection. <paramref name="root"/> is "/" in production, a temp dir in tests.</summary>
public sealed class RuntimeInfoProvider(string root, EnvLookup env, StateService state)
{
    private const long Unlimited = 1L << 62;

    public RuntimeInfo Get()
    {
        var orchestrator = DetectOrchestrator();
        return new RuntimeInfo(
            Hostname: Environment.MachineName,
            ContainerRuntime: DetectContainerRuntime(),
            Orchestrator: orchestrator,
            Pod: orchestrator == "kubernetes" ? ReadPod() : null,
            Cgroup: ReadCgroup(),
            Os: ReadOs(),
            Dotnet: RuntimeInformation.FrameworkDescription,
            Gc: GCSettings.IsServerGC ? "server" : "workstation",
            ProcessorCount: Environment.ProcessorCount,
            Uid: ReadStatusId("Uid:"),
            Gid: ReadStatusId("Gid:"),
            Pid: Environment.ProcessId,
            LoadAverage1m: ReadLoadAverage(),
            EnvHints: new Dictionary<string, string?>
            {
                ["KUBERNETES_SERVICE_HOST"] = env("KUBERNETES_SERVICE_HOST"),
                ["container"] = env("container"),
                ["HOSTNAME"] = env("HOSTNAME"),
            },
            State: state.Snapshot());
    }

    private string P(string relative) => Path.Combine(root, relative);

    private string? ReadText(string relative)
    {
        try
        {
            var path = P(relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DetectContainerRuntime()
    {
        if (File.Exists(P("run/.containerenv"))) return "podman";
        if (File.Exists(P(".dockerenv"))) return "docker";
        if (AppSettings.Blank(env("container")) is { } fromEnv) return fromEnv;
        var cgroup = ReadText("proc/self/cgroup") ?? "";
        if (cgroup.Contains("crio")) return "cri-o";
        if (cgroup.Contains("containerd")) return "containerd";
        if (cgroup.Contains("docker")) return "docker";
        if (cgroup.Contains("kubepods")) return "kubernetes";
        return "none";
    }

    private string DetectOrchestrator() =>
        AppSettings.Blank(env("KUBERNETES_SERVICE_HOST")) is not null
        || File.Exists(P("var/run/secrets/kubernetes.io/serviceaccount/namespace"))
            ? "kubernetes"
            : "none";

    private PodInfo ReadPod() => new(
        AppSettings.Blank(env("POD_NAME")),
        AppSettings.Blank(env("POD_NAMESPACE")) ?? AppSettings.Blank(ReadText("var/run/secrets/kubernetes.io/serviceaccount/namespace")),
        AppSettings.Blank(env("NODE_NAME")),
        AppSettings.Blank(env("POD_IP")),
        AppSettings.Blank(env("SERVICE_ACCOUNT")));

    private CgroupInfo ReadCgroup()
    {
        if (File.Exists(P("sys/fs/cgroup/cgroup.controllers")))
        {
            double? cpu = null;
            var parts = ReadText("sys/fs/cgroup/cpu.max")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is [var q, var p] && long.TryParse(q, out var quota) && long.TryParse(p, out var period) && quota > 0 && period > 0)
                cpu = Math.Round((double)quota / period, 2);
            return new CgroupInfo("v2", cpu, Limit(ReadText("sys/fs/cgroup/memory.max")), Limit(ReadText("sys/fs/cgroup/memory.current")));
        }
        if (Directory.Exists(P("sys/fs/cgroup/cpu")) || Directory.Exists(P("sys/fs/cgroup/memory")))
        {
            double? cpu = null;
            if (Limit(ReadText("sys/fs/cgroup/cpu/cpu.cfs_quota_us")) is long quota and > 0
                && Limit(ReadText("sys/fs/cgroup/cpu/cpu.cfs_period_us")) is long period and > 0)
                cpu = Math.Round((double)quota / period, 2);
            return new CgroupInfo("v1", cpu,
                Limit(ReadText("sys/fs/cgroup/memory/memory.limit_in_bytes")),
                Limit(ReadText("sys/fs/cgroup/memory/memory.usage_in_bytes")));
        }
        return new CgroupInfo("none", null, null, null);
    }

    /// <summary>"max", negative, unparsable or ≥ 2^62 all mean "no limit".</summary>
    internal static long? Limit(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < Unlimited ? v : null;

    private string ReadOs()
    {
        foreach (var line in (ReadText("etc/os-release") ?? "").Split('\n'))
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                return line["PRETTY_NAME=".Length..].Trim().Trim('"');
        return RuntimeInformation.OSDescription;
    }

    private int? ReadStatusId(string prefix)
    {
        foreach (var line in (ReadText("proc/self/status") ?? "").Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var fields = line[prefix.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0 && int.TryParse(fields[0], out var id) ? id : null;
            }
        return null;
    }

    private double? ReadLoadAverage()
    {
        var first = ReadText("proc/loadavg")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
```

`src/AzureDash/Endpoints/RuntimeEndpoints.cs`:
```csharp
using AzureDash.Runtime;

namespace AzureDash.Endpoints;

public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime", (RuntimeInfoProvider p) => Results.Json(p.Get()));
        return app;
    }
}
```

In `src/AzureDash/Program.cs`:
- add `using AzureDash.Runtime;`
- after the `IProcessExit` registration, add:
  ```csharp
  builder.Services.AddSingleton(sp => new RuntimeInfoProvider("/", sp.GetRequiredService<EnvLookup>(), sp.GetRequiredService<StateService>()));
  ```
- after `app.MapControlsEndpoints();`, add:
  ```csharp
  app.MapRuntimeEndpoints();
  ```

- [ ] **Step 4: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add runtime info (container, pod, cgroup) API"
```

---

### Task 4: Layout, CSS, htmx, Runtime and Controls pages

**Files:**
- Create: `src/AzureDash/AppInfo.cs`, `src/AzureDash/Components/{_Imports.razor,Fmt.cs}`
- Create: `src/AzureDash/Components/Partials/{RuntimePartial,ControlsPartial}.razor`
- Create: `src/AzureDash/Pages/{_ViewImports.cshtml,_ViewStart.cshtml,Shared/_Layout.cshtml,Index.cshtml}`
- Create: `src/AzureDash/wwwroot/css/site.css`, `src/AzureDash/wwwroot/js/htmx.min.js` (downloaded)
- Modify: `src/AzureDash/Endpoints/{RuntimeEndpoints,ControlsEndpoints}.cs`, `src/AzureDash/Program.cs`
- Test: `tests/AzureDash.Tests/PagesTests.cs`, `tests/AzureDash.Tests/FmtTests.cs`

**Interfaces:**
- Consumes: `RuntimeInfoProvider`, `StateService`, `StateSnapshot`, `Htmx.IsHtmx` (Tasks 2–3).
- Produces:
  - `AppInfo.Version`
  - `Fmt.Or(string?)`, `Fmt.Bytes(long?, string nullText = "—")`, `Fmt.Cores(double?)`, `Fmt.Time(DateTimeOffset?)`, `Fmt.Duration(double seconds)`, `Fmt.Until(DateTimeOffset target, DateTimeOffset now)`
  - Components `RuntimePartial(Info: RuntimeInfo)` and `ControlsPartial(Snapshot: StateSnapshot)`. The root element of `ControlsPartial` is `<section id="controls">`.
  - Layout `ViewData["Nav"]` ∈ `runtime|azure|identity`
  - Routes `GET /`, `GET /partials/runtime`, `GET /partials/controls`

- [ ] **Step 1: Write the failing tests**

`tests/AzureDash.Tests/FmtTests.cs`:
```csharp
using AzureDash.Components;

namespace AzureDash.Tests;

public class FmtTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(512L, "512 B")]
    [InlineData(536870912L, "512.0 MiB")]
    [InlineData(1610612736L, "1.5 GiB")]
    public void Bytes(long? value, string expected) => Assert.Equal(expected, Fmt.Bytes(value));

    [Fact]
    public void Bytes_null_text() => Assert.Equal("unlimited", Fmt.Bytes(null, "unlimited"));

    [Fact]
    public void Cores() { Assert.Equal("0.5 cores", Fmt.Cores(0.5)); Assert.Equal("unlimited", Fmt.Cores(null)); }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(125, "2m 5s")]
    [InlineData(7260, "2h 1m")]
    [InlineData(90000, "1d 1h")]
    public void Duration(double seconds, string expected) => Assert.Equal(expected, Fmt.Duration(seconds));

    [Fact]
    public void Time_and_until()
    {
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal("2026-01-02 03:04:05 UTC", Fmt.Time(t));
        Assert.Equal("never", Fmt.Time(null));
        Assert.Equal("in 1m 0s", Fmt.Until(t.AddMinutes(1), t));
        Assert.Equal("expired", Fmt.Until(t, t.AddSeconds(1)));
    }

    [Fact]
    public void Or() { Assert.Equal("—", Fmt.Or(" ")); Assert.Equal("x", Fmt.Or("x")); }
}
```

`tests/AzureDash.Tests/PagesTests.cs`:
```csharp
using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class PagesTests
{
    [Fact]
    public async Task Index_renders_runtime_and_controls()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/");
        Assert.Contains("<title>Runtime · azure-dash</title>", html);
        Assert.Contains("hx-get=\"/partials/runtime\"", html);
        Assert.Contains("id=\"controls\"", html);
        Assert.Contains("Hostname", html);
        Assert.Contains("/js/htmx.min.js", html);
    }

    [Fact]
    public async Task Runtime_partial_is_a_fragment()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/partials/runtime");
        Assert.Contains("Hostname", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Htmx_control_post_returns_controls_fragment()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().HxPostFormAsync("/controls/readiness", ("enabled", "false"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("id=\"controls\"", html);
        Assert.Contains("Restore readiness", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Htmx_crash_returns_html_span()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().HxPostFormAsync("/controls/crash", ("exit_code", "3"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("Exiting with code 3", await r.Content.ReadAsStringAsync());
        await f.Exit.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Controls_disabled_message()
    {
        using var f = new AppFactory { Settings = new() { ControlsEnabled = false, ImdsEnabled = false } };
        var html = await f.CreateClient().GetStringAsync("/partials/controls");
        Assert.Contains("Controls are disabled (CONTROLS_ENABLED=false)", html);
        Assert.DoesNotContain("hx-post", html);
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/htmx.min.js")]
    public async Task Static_assets_are_served(string path)
    {
        using var f = new AppFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync(path)).StatusCode);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~PagesTests|FullyQualifiedName~FmtTests"`
Expected: a build error, because `Fmt` doesn't exist yet.

- [ ] **Step 3: Vendor htmx**

```bash
mkdir -p src/AzureDash/wwwroot/js src/AzureDash/wwwroot/css
curl -fsSL https://cdn.jsdelivr.net/npm/htmx.org@2.0.11/dist/htmx.min.js -o src/AzureDash/wwwroot/js/htmx.min.js
head -c 120 src/AzureDash/wwwroot/js/htmx.min.js
```
Expected: minified JavaScript that begins with `var htmx=function()`, or something similar.

- [ ] **Step 4: Write `AppInfo`, `Fmt` and the component imports**

`src/AzureDash/AppInfo.cs`:
```csharp
using System.Reflection;

namespace AzureDash;

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
```

`src/AzureDash/Components/Fmt.cs`:
```csharp
using System.Globalization;

namespace AzureDash.Components;

public static class Fmt
{
    static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    public static string Bytes(long? bytes, string nullText = "—")
    {
        if (bytes is not long v) return nullText;
        double d = v;
        var i = 0;
        while (d >= 1024 && i < Units.Length - 1) { d /= 1024; i++; }
        return i == 0 ? $"{v} B" : string.Create(CultureInfo.InvariantCulture, $"{d:0.0} {Units[i]}");
    }

    public static string Cores(double? cores) =>
        cores is double c ? string.Create(CultureInfo.InvariantCulture, $"{c:0.##} cores") : "unlimited";

    public static string Time(DateTimeOffset? t) =>
        t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "never";

    public static string Duration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public static string Until(DateTimeOffset target, DateTimeOffset now)
    {
        var d = target - now;
        return d <= TimeSpan.Zero ? "expired" : $"in {Duration(d.TotalSeconds)}";
    }
}
```

`src/AzureDash/Components/_Imports.razor`:
```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using AzureDash.Components
```

- [ ] **Step 5: Write the Runtime and Controls components**

`src/AzureDash/Components/Partials/RuntimePartial.razor`:
```razor
@using AzureDash.Runtime
<dl class="kv">
    <dt>Probes</dt>
    <dd>
        <span class="badge @(Info.State.Live ? "ok" : "bad")">live: @(Info.State.Live ? "OK" : "FAILING")</span>
        <span class="badge @(Info.State.Ready ? "ok" : "bad")">ready: @(Info.State.Ready ? "OK" : "FAILING")</span>
        <span class="badge @(Info.State.CpuLoad.Active ? "warn" : "ok")">cpu load: @(Info.State.CpuLoad.Active ? $"{Info.State.CpuLoad.Workers} worker(s)" : "off")</span>
    </dd>
    <dt>Hostname</dt><dd>@Info.Hostname</dd>
    <dt>Container runtime</dt><dd>@Info.ContainerRuntime</dd>
    <dt>Orchestrator</dt><dd>@Info.Orchestrator</dd>
    @if (Info.Pod is { } pod)
    {
        <dt>Pod</dt><dd>@Fmt.Or(pod.Name)</dd>
        <dt>Namespace</dt><dd>@Fmt.Or(pod.Namespace)</dd>
        <dt>Node</dt><dd>@Fmt.Or(pod.Node)</dd>
        <dt>Pod IP</dt><dd>@Fmt.Or(pod.Ip)</dd>
        <dt>Service account</dt><dd>@Fmt.Or(pod.ServiceAccount)</dd>
    }
    <dt>cgroup</dt><dd>@Info.Cgroup.Version</dd>
    <dt>CPU limit</dt><dd>@Fmt.Cores(Info.Cgroup.CpuLimitCores)</dd>
    <dt>Memory</dt><dd>@Fmt.Bytes(Info.Cgroup.MemoryUsageBytes) used / @Fmt.Bytes(Info.Cgroup.MemoryLimitBytes, "unlimited")</dd>
    <dt>OS</dt><dd>@Info.Os</dd>
    <dt>.NET</dt><dd>@Info.Dotnet (@Info.Gc GC)</dd>
    <dt>CPUs visible</dt><dd>@Info.ProcessorCount</dd>
    <dt>Load (1m)</dt><dd>@(Info.LoadAverage1m?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "—")</dd>
    <dt>uid / gid / pid</dt><dd>@Fmt.Or(Info.Uid?.ToString()) / @Fmt.Or(Info.Gid?.ToString()) / @Info.Pid</dd>
    <dt>Started</dt><dd>@Fmt.Time(Info.State.StartedAt) (up @Fmt.Duration(Info.State.UptimeSeconds))</dd>
</dl>

@code {
    [Parameter, EditorRequired] public RuntimeInfo Info { get; set; } = default!;
}
```

`src/AzureDash/Components/Partials/ControlsPartial.razor`:
```razor
@using AzureDash.State
<section id="controls" class="card">
    <h2>Controls</h2>
    @if (!Snapshot.ControlsEnabled)
    {
        <p class="muted">Controls are disabled (CONTROLS_ENABLED=false).</p>
    }
    else
    {
        <div class="controls-grid">
            <div class="control">
                <h3>Liveness <span class="badge @(Snapshot.Live ? "ok" : "bad")">@(Snapshot.Live ? "OK" : "FAILING")</span></h3>
                <p class="muted">When failing, /healthz/live returns 503; after 3 failed probes the kubelet restarts the container.</p>
                <button hx-post="/controls/liveness" hx-vals="@Vals(!Snapshot.Live)" hx-target="#controls" hx-swap="outerHTML">@(Snapshot.Live ? "Fail liveness" : "Restore liveness")</button>
            </div>
            <div class="control">
                <h3>Readiness <span class="badge @(Snapshot.Ready ? "ok" : "bad")">@(Snapshot.Ready ? "OK" : "FAILING")</span></h3>
                <p class="muted">When failing, /healthz/ready returns 503 and the pod is removed from the Service's endpoints.</p>
                <button hx-post="/controls/readiness" hx-vals="@Vals(!Snapshot.Ready)" hx-target="#controls" hx-swap="outerHTML">@(Snapshot.Ready ? "Fail readiness" : "Restore readiness")</button>
            </div>
            <div class="control">
                <h3>CPU load <span class="badge @(Snapshot.CpuLoad.Active ? "warn" : "ok")">@(Snapshot.CpuLoad.Active ? $"{Snapshot.CpuLoad.Workers} busy" : "idle")</span></h3>
                <p class="muted">Spins busy threads to drive CPU usage; watch it with kubectl top pod or an HPA.</p>
                @if (Snapshot.CpuLoad.Active)
                {
                    <button hx-post="/controls/cpu" hx-vals="@Vals(false)" hx-target="#controls" hx-swap="outerHTML">Stop</button>
                }
                else
                {
                    <form hx-post="/controls/cpu" hx-target="#controls" hx-swap="outerHTML">
                        <input type="hidden" name="enabled" value="true" />
                        <label>Workers <input type="number" name="workers" min="1" max="64" value="1" /></label>
                        <button type="submit">Start</button>
                    </form>
                }
            </div>
            <div class="control">
                <h3>Crash</h3>
                <p class="muted">Exits the process with the given code; Kubernetes restarts it and the restart count goes up.</p>
                <form hx-post="/controls/crash" hx-target="#crash-result" hx-confirm="Really exit the process?">
                    <label>Exit code <input type="number" name="exit_code" min="0" max="255" value="1" /></label>
                    <button type="submit" class="danger">Crash</button>
                </form>
                <div id="crash-result"></div>
            </div>
        </div>
    }
</section>

@code {
    [Parameter, EditorRequired] public StateSnapshot Snapshot { get; set; } = default!;

    static string Vals(bool enabled) => enabled ? "{\"enabled\":\"true\"}" : "{\"enabled\":\"false\"}";
}
```

- [ ] **Step 6: Write the Razor Pages and the layout**

`src/AzureDash/Pages/_ViewImports.cshtml`:
```cshtml
@using AzureDash
@using AzureDash.Components.Partials
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

`src/AzureDash/Pages/_ViewStart.cshtml`:
```cshtml
@{ Layout = "_Layout"; }
```

`src/AzureDash/Pages/Shared/_Layout.cshtml`:
```cshtml
@{ var nav = ViewData["Nav"] as string; }
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>@ViewData["Title"] · azure-dash</title>
    <link rel="stylesheet" href="~/css/site.css" />
    <script src="~/js/htmx.min.js"></script>
</head>
<body>
    <header class="topbar">
        <span class="brand">azure-dash</span>
        <nav>
            <a href="/" class="@(nav == "runtime" ? "active" : null)">Runtime</a>
            <a href="/azure" class="@(nav == "azure" ? "active" : null)">Azure</a>
            <a href="/identity" class="@(nav == "identity" ? "active" : null)">Identity</a>
        </nav>
        <span class="version">v@(AppInfo.Version)</span>
    </header>
    <main>
        @RenderBody()
    </main>
</body>
</html>
```

`src/AzureDash/Pages/Index.cshtml`:
```cshtml
@page "/"
@using AzureDash.Runtime
@using AzureDash.State
@inject RuntimeInfoProvider Runtime
@inject StateService State
@{
    ViewData["Title"] = "Runtime";
    ViewData["Nav"] = "runtime";
}
<div class="grid">
    <section class="card">
        <h2>Runtime</h2>
        <div id="runtime" hx-get="/partials/runtime" hx-trigger="every 5s" hx-swap="innerHTML">
            <component type="typeof(RuntimePartial)" render-mode="Static" param-Info="@(Runtime.Get())" />
        </div>
    </section>
    <component type="typeof(ControlsPartial)" render-mode="Static" param-Snapshot="@(State.Snapshot())" />
</div>
```

- [ ] **Step 7: Write the stylesheet**

`src/AzureDash/wwwroot/css/site.css`:
```css
:root {
  --bg: #f5f6f8; --card: #ffffff; --text: #1b1b1f; --muted: #5f6368; --line: #e3e5e8;
  --accent: #0078d4; --ok: #107c10; --bad: #c50f1f; --warn: #8a5300; --topbar: #1b1a19;
  --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
.topbar { display: flex; align-items: center; gap: 24px; padding: 0 24px; height: 52px; background: var(--topbar); color: #fff; }
.topbar .brand { font-weight: 700; letter-spacing: .02em; }
.topbar nav { display: flex; gap: 18px; flex: 1; }
.topbar nav a { color: #d0d0d0; text-decoration: none; padding: 15px 0 12px; border-bottom: 3px solid transparent; }
.topbar nav a.active, .topbar nav a:hover { color: #fff; border-bottom-color: var(--accent); }
.topbar .version { color: #a0a0a0; font-size: 13px; }
main { max-width: 1100px; margin: 24px auto; padding: 0 16px; }
.grid { display: grid; gap: 16px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 8px; padding: 16px 20px; min-width: 0; }
.card h2 { margin: 0 0 12px; font-size: 17px; }
.card h3 { margin: 16px 0 6px; font-size: 15px; display: flex; gap: 8px; align-items: center; }
.control h3 { margin-top: 0; }
.muted { color: var(--muted); font-size: 13px; }
dl.kv { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0; }
dl.kv dt { color: var(--muted); }
dl.kv dd { margin: 0; font-family: var(--mono); font-size: 13px; overflow-wrap: anywhere; min-width: 0; }
.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th { text-align: left; color: var(--muted); font-weight: 600; border-bottom: 1px solid var(--line); padding: 6px 8px; white-space: nowrap; }
td { border-bottom: 1px solid var(--line); padding: 6px 8px; font-family: var(--mono); }
.badge { display: inline-block; padding: 1px 8px; border-radius: 10px; font: 600 12px system-ui, sans-serif; }
.badge.ok { background: #dff6dd; color: var(--ok); }
.badge.bad { background: #fde7e9; color: var(--bad); }
.badge.warn { background: #fff4ce; color: var(--warn); }
.controls-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; }
.control { border: 1px solid var(--line); border-radius: 6px; padding: 12px; }
button { font: inherit; padding: 6px 14px; border-radius: 4px; border: 1px solid var(--accent); background: var(--accent); color: #fff; cursor: pointer; }
button:hover { filter: brightness(1.1); }
button.secondary { background: #fff; color: var(--accent); }
button.danger { background: var(--bad); border-color: var(--bad); }
input[type=number] { width: 72px; font: inherit; padding: 4px 6px; margin: 0 8px 8px 4px; }
.error { background: #fde7e9; color: var(--bad); border-radius: 4px; padding: 8px 12px; margin-bottom: 8px; font: 13px/1.4 var(--mono); white-space: pre-wrap; overflow-wrap: anywhere; }
.warning { background: #fff4ce; color: var(--warn); border-radius: 4px; padding: 8px 12px; margin-bottom: 8px; font-size: 13px; }
.header-row { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
.header-row h2 { margin: 0; }
@media (max-width: 600px) {
  .topbar { gap: 12px; padding: 0 16px; }
  .topbar .version { display: none; }
  dl.kv { grid-template-columns: 1fr; gap: 2px; }
  dl.kv dd { margin-bottom: 6px; }
}
```

- [ ] **Step 8: Add the fragment endpoints and the htmx response for controls**

Replace `src/AzureDash/Endpoints/RuntimeEndpoints.cs` with:
```csharp
using AzureDash.Components.Partials;
using AzureDash.Runtime;
using AzureDash.State;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime", (RuntimeInfoProvider p) => Results.Json(p.Get()));
        app.MapGet("/partials/runtime", (RuntimeInfoProvider p) => new RazorComponentResult<RuntimePartial>(new { Info = p.Get() }));
        app.MapGet("/partials/controls", (StateService s) => new RazorComponentResult<ControlsPartial>(new { Snapshot = s.Snapshot() }));
        return app;
    }
}
```

In `src/AzureDash/Endpoints/ControlsEndpoints.cs`:
- add `using AzureDash.Components.Partials;` and `using Microsoft.AspNetCore.Http.HttpResults;`
- replace the `Respond` method with:
```csharp
    static IResult Respond(HttpContext ctx, StateService s)
    {
        var snapshot = s.Snapshot();
        return Htmx.IsHtmx(ctx.Request)
            ? new RazorComponentResult<ControlsPartial>(new { Snapshot = snapshot })
            : Results.Json(snapshot);
    }
```

In `src/AzureDash/Program.cs`:
- after `ConfigureHttpJsonOptions(...)`, add:
  ```csharp
  builder.Services.AddRazorPages();
  builder.Services.AddRazorComponents();
  ```
- right after `var app = builder.Build();`, add:
  ```csharp
  app.UseStaticFiles();
  app.MapRazorPages();
  ```

- [ ] **Step 9: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 10: Look at it in a browser**

```bash
ASPNETCORE_HTTP_PORTS=8080 dotnet run --project src/AzureDash &
curl --retry 20 --retry-connrefused --retry-delay 1 -fsS http://localhost:8080/ | grep -c 'id="controls"'
curl -fsS -X POST -H 'HX-Request: true' -d enabled=false http://localhost:8080/controls/readiness | grep -o 'Restore readiness'
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/healthz/ready
kill %1
```
Expected: `1`, then `Restore readiness`, then `503`. Open http://localhost:8080 during the run if you want to see the page.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: add layout, runtime page, and htmx controls"
```

---

### Task 5: Azure packages, error translation, TTL cache

**Files:**
- Modify: `src/AzureDash/AzureDash.csproj` (package references)
- Create: `src/AzureDash/Inventory/{AzureError,TtlCache}.cs`
- Test: `tests/AzureDash.Tests/AzureErrorTests.cs`, `tests/AzureDash.Tests/TtlCacheTests.cs`

**Interfaces:**
- Produces:
  - `AzureError(string message, Exception? inner = null) : Exception`, with `AzureError.From(Exception) → AzureError` (adds actionable hints for 403, 401 and authentication failures) and `internal static string FirstLine(string)`
  - `record CacheEntry(object? Value, DateTimeOffset? FetchedAt, string? Error)`
  - `TtlCache(TimeSpan ttl, TimeProvider time)` with:
    - `Task<CacheEntry> GetAsync(string key, Func<CancellationToken, Task<object>> loader, bool force = false, CancellationToken ct = default)`
    - `void InvalidateAll()`
  - Cache rules:
    - A failed load stores the **previous good value** along with the error.
    - An `AzureError` message is stored as it is; any other exception is stored as `"{Type}: {Message}"`.
    - A load that was in flight when `InvalidateAll` ran is not stored.

- [ ] **Step 1: Add the Azure package references**

In `src/AzureDash/AzureDash.csproj`, add this `ItemGroup`. Versions come from `Directory.Packages.props`.
```xml
  <ItemGroup>
    <PackageReference Include="Azure.Identity" />
    <PackageReference Include="Azure.ResourceManager" />
    <PackageReference Include="Azure.ResourceManager.Compute" />
    <PackageReference Include="Azure.ResourceManager.Network" />
    <PackageReference Include="Azure.ResourceManager.Storage" />
    <PackageReference Include="Azure.ResourceManager.ResourceGraph" />
  </ItemGroup>
```
Run: `dotnet build`
Expected: `Build succeeded` with 0 errors. NuGet audit warnings, if any, are allowed.

- [ ] **Step 2: Write the failing tests**

`tests/AzureDash.Tests/AzureErrorTests.cs`:
```csharp
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
```

`tests/AzureDash.Tests/TtlCacheTests.cs`:
```csharp
using AzureDash.Inventory;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class TtlCacheTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    int _calls;

    TtlCache Cache() => new(TimeSpan.FromSeconds(60), _time);

    Func<CancellationToken, Task<object>> Returns(object value) => _ => { _calls++; return Task.FromResult(value); };
    Func<CancellationToken, Task<object>> Throws(Exception ex) => _ => { _calls++; return Task.FromException<object>(ex); };

    [Fact]
    public async Task Caches_within_ttl()
    {
        var c = Cache();
        var first = await c.GetAsync("k", Returns("a"));
        var second = await c.GetAsync("k", Returns("b"));
        Assert.Equal("a", first.Value);
        Assert.Equal("a", second.Value);
        Assert.Equal(_time.Now, second.FetchedAt);
        Assert.Null(second.Error);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Reloads_after_ttl_and_on_force()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("a"));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("b", (await c.GetAsync("k", Returns("b"))).Value);
        Assert.Equal("c", (await c.GetAsync("k", Returns("c"), force: true)).Value);
        Assert.Equal(3, _calls);
    }

    [Fact]
    public async Task Error_keeps_previous_value()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("good"));
        _time.Advance(TimeSpan.FromSeconds(5));
        var entry = await c.GetAsync("k", Throws(new AzureError("denied")), force: true);
        Assert.Equal("good", entry.Value);
        Assert.Equal("denied", entry.Error);
        Assert.Equal(_time.Now, entry.FetchedAt);
    }

    [Fact]
    public async Task First_error_has_no_value_and_names_exception_type()
    {
        var entry = await Cache().GetAsync("k", Throws(new InvalidOperationException("boom")));
        Assert.Null(entry.Value);
        Assert.Equal("InvalidOperationException: boom", entry.Error);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var c = Cache();
        await c.GetAsync("a", Throws(new AzureError("a failed")));
        var b = await c.GetAsync("b", Returns("fine"));
        Assert.Equal("fine", b.Value);
        Assert.Null(b.Error);
    }

    [Fact]
    public async Task Invalidate_all_forces_reload()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("a"));
        c.InvalidateAll();
        Assert.Equal("b", (await c.GetAsync("k", Returns("b"))).Value);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_load()
    {
        var c = Cache();
        var gate = new TaskCompletionSource<object>();
        Func<CancellationToken, Task<object>> slow = _ => { Interlocked.Increment(ref _calls); return gate.Task; };
        var t1 = c.GetAsync("k", slow);
        var t2 = c.GetAsync("k", slow);
        gate.SetResult("v");
        Assert.Equal("v", (await t1).Value);
        Assert.Equal("v", (await t2).Value);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Load_in_flight_during_invalidate_is_not_stored()
    {
        var c = Cache();
        var gate = new TaskCompletionSource<object>();
        var inflight = c.GetAsync("k", _ => gate.Task);
        c.InvalidateAll();
        gate.SetResult("stale");
        Assert.Equal("stale", (await inflight).Value);
        Assert.Equal("fresh", (await c.GetAsync("k", Returns("fresh"))).Value);
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~AzureErrorTests|FullyQualifiedName~TtlCacheTests"`
Expected: a build error, because `AzureError` and `TtlCache` don't exist yet.

- [ ] **Step 4: Implement `AzureError`**

`src/AzureDash/Inventory/AzureError.cs`:
```csharp
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
```

- [ ] **Step 5: Implement `TtlCache`**

`src/AzureDash/Inventory/TtlCache.cs`:
```csharp
using System.Collections.Concurrent;

namespace AzureDash.Inventory;

public sealed record CacheEntry(object? Value, DateTimeOffset? FetchedAt, string? Error);

/// <summary>Per-key TTL cache: one load at a time per key; failures keep the last good value.</summary>
public sealed class TtlCache(TimeSpan ttl, TimeProvider time)
{
    private sealed record Slot(CacheEntry Entry, DateTimeOffset FreshUntil, long Epoch);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private long _epoch;

    public async Task<CacheEntry> GetAsync(
        string key, Func<CancellationToken, Task<object>> loader, bool force = false, CancellationToken ct = default)
    {
        if (!force && TryFresh(key, out var fresh)) return fresh;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!force && TryFresh(key, out fresh)) return fresh;

            var epoch = Interlocked.Read(ref _epoch);
            var previous = _slots.TryGetValue(key, out var old) ? old.Entry.Value : null;
            CacheEntry entry;
            try
            {
                entry = new CacheEntry(await loader(ct), time.GetUtcNow(), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var message = ex is AzureError ? ex.Message : $"{ex.GetType().Name}: {AzureError.FirstLine(ex.Message)}";
                entry = new CacheEntry(previous, time.GetUtcNow(), message);
            }

            if (Interlocked.Read(ref _epoch) == epoch)
                _slots[key] = new Slot(entry, time.GetUtcNow() + ttl, epoch);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _epoch);
        _slots.Clear();
    }

    private bool TryFresh(string key, out CacheEntry entry)
    {
        if (_slots.TryGetValue(key, out var slot) && slot.Epoch == Interlocked.Read(ref _epoch) && time.GetUtcNow() < slot.FreshUntil)
        {
            entry = slot.Entry;
            return true;
        }
        entry = null!;
        return false;
    }
}
```

- [ ] **Step 6: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add Azure SDK packages, error translation, and TTL cache"
```

---

### Task 6: Inventory service, fake provider, Azure API and panels

**Files:**
- Create: `src/AzureDash/Inventory/{Models,AzureKinds,IAzureProvider,AzureService}.cs`
- Create: `src/AzureDash/Endpoints/AzureEndpoints.cs`
- Create: `src/AzureDash/Components/Partials/AzurePanel.razor`
- Create: `src/AzureDash/Components/Partials/Inventory/{SubscriptionTable,ResourceGroupsTable,VmsTable,StorageAccountsTable,VnetsTable,SubnetsTable,NsgRulesTable,ResourceGraphTable}.razor`
- Create: `src/AzureDash/Pages/Azure.cshtml`
- Modify: `src/AzureDash/Program.cs`, `tests/AzureDash.Tests/Support/AppFactory.cs`
- Create: `tests/AzureDash.Tests/Support/FakeAzureProvider.cs`
- Test: `tests/AzureDash.Tests/AzureServiceTests.cs`, `tests/AzureDash.Tests/AzureApiTests.cs`

**Interfaces:**
- Consumes: `TtlCache`, `CacheEntry`, `AzureError` (Task 5), `Fmt`, `Htmx` (Tasks 2 and 4), `AppSettings`.
- Produces:
  - **Model records:**
    - `SubscriptionInfo(string Id, string DisplayName, string? State, string? TenantId, string Source, string? Warning)`
    - `ResourceGroupInfo(string Name, string Location, string? ProvisioningState)`
    - `VmInfo(string Kind, string Name, string ResourceGroup, string Location, string? Zones, string? Size, string? PowerState, int? Capacity)`, where `Kind` is `"VM"` or `"VMSS"`
    - `StorageAccountInfo(string Name, string ResourceGroup, string Location, string? Kind, string? Sku, string? AccessTier, string? PublicNetworkAccess)`
    - `VnetInfo(string Name, string ResourceGroup, string Location, string AddressSpace, int SubnetCount)`
    - `SubnetInfo(string Vnet, string Name, string? AddressPrefix, string? Nsg, string? RouteTable)`
    - `NsgRuleInfo(string Nsg, string Rule, string? Direction, int? Priority, string? Access, string? Protocol, string Ports, string Source, string Destination)`
    - `ResourceTypeCount(string Type, long Count)`
  - **`AzureKinds`:** `All` (ordered `(Kind, Title)` pairs: `subscription, resourcegroups, vms, storageaccounts, vnets, subnets, nsgrules, resourcegraph`), `IsKnown(string)`, `Title(string)`.
  - **`IAzureProvider`:** `GetSubscriptionAsync`, `ListResourceGroupsAsync`, `ListVmsAsync`, `ListStorageAccountsAsync`, `ListVnetsAsync`, `ListSubnetsAsync`, `ListNsgRulesAsync`, `ResourceGraphSummaryAsync`. Every method takes `(CancellationToken)`. List methods return `Task<IReadOnlyList<T>>`.
  - **`AzureService(Func<IAzureProvider> createProvider, TtlCache cache, ILogger<AzureService> log)`:**
    - `FetchAsync(string kind, bool force, CancellationToken) → Task<CacheEntry>`
    - `InvalidateAll()`
    - Creates the provider lazily; a provider that fails to construct is retried on the next fetch.
  - **DI:** registers `Func<IAzureProvider>`. In this task it's a stub that throws `AzureError("no Azure provider is registered")`; Task 8 replaces it.
  - **Routes:** `GET /azure`, `GET /api/azure/{kind}[?refresh=true]`, `POST /api/azure/refresh` (204), `GET /partials/azure/{kind}[?refresh=1]`.
  - **`AzureApiResponse(string Kind, DateTimeOffset? FetchedAt, string? Error, object? Items)`.**
  - **Test helper:** `AppFactory.Azure` (a `FakeAzureProvider` with `Fail(params string[] kinds)`, `Succeed(params string[] kinds)`, `EmptyLists`, `Calls`).

- [ ] **Step 1: Write the inventory contracts**

`src/AzureDash/Inventory/Models.cs`:
```csharp
namespace AzureDash.Inventory;

public sealed record SubscriptionInfo(string Id, string DisplayName, string? State, string? TenantId, string Source, string? Warning);
public sealed record ResourceGroupInfo(string Name, string Location, string? ProvisioningState);
public sealed record VmInfo(string Kind, string Name, string ResourceGroup, string Location, string? Zones, string? Size, string? PowerState, int? Capacity);
public sealed record StorageAccountInfo(string Name, string ResourceGroup, string Location, string? Kind, string? Sku, string? AccessTier, string? PublicNetworkAccess);
public sealed record VnetInfo(string Name, string ResourceGroup, string Location, string AddressSpace, int SubnetCount);
public sealed record SubnetInfo(string Vnet, string Name, string? AddressPrefix, string? Nsg, string? RouteTable);
public sealed record NsgRuleInfo(string Nsg, string Rule, string? Direction, int? Priority, string? Access, string? Protocol, string Ports, string Source, string Destination);
public sealed record ResourceTypeCount(string Type, long Count);
```

`src/AzureDash/Inventory/AzureKinds.cs`:
```csharp
namespace AzureDash.Inventory;

public static class AzureKinds
{
    public static readonly IReadOnlyList<(string Kind, string Title)> All =
    [
        ("subscription", "Subscription"),
        ("resourcegroups", "Resource groups"),
        ("vms", "Virtual machines & scale sets"),
        ("storageaccounts", "Storage accounts"),
        ("vnets", "Virtual networks"),
        ("subnets", "Subnets"),
        ("nsgrules", "Network security group rules"),
        ("resourcegraph", "Resource Graph: resources by type"),
    ];

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == kind);

    public static string Title(string kind) => All.First(k => k.Kind == kind).Title;
}
```

`src/AzureDash/Inventory/IAzureProvider.cs`:
```csharp
namespace AzureDash.Inventory;

public interface IAzureProvider
{
    Task<SubscriptionInfo> GetSubscriptionAsync(CancellationToken ct);
    Task<IReadOnlyList<ResourceGroupInfo>> ListResourceGroupsAsync(CancellationToken ct);
    Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct);
    Task<IReadOnlyList<StorageAccountInfo>> ListStorageAccountsAsync(CancellationToken ct);
    Task<IReadOnlyList<VnetInfo>> ListVnetsAsync(CancellationToken ct);
    Task<IReadOnlyList<SubnetInfo>> ListSubnetsAsync(CancellationToken ct);
    Task<IReadOnlyList<NsgRuleInfo>> ListNsgRulesAsync(CancellationToken ct);
    Task<IReadOnlyList<ResourceTypeCount>> ResourceGraphSummaryAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Write the fake provider and update `AppFactory`**

`tests/AzureDash.Tests/Support/FakeAzureProvider.cs`:
```csharp
using AzureDash.Inventory;

namespace AzureDash.Tests.Support;

public sealed class FakeAzureProvider : IAzureProvider
{
    private readonly HashSet<string> _failing = [];
    public Dictionary<string, int> Calls { get; } = new();
    public bool EmptyLists { get; set; }
    public string FailureMessage { get; set; } = "RequestFailedException: AuthorizationFailed (403). Grant the identity Reader";

    public void Fail(params string[] kinds) { lock (_failing) _failing.UnionWith(kinds); }
    public void Succeed(params string[] kinds) { lock (_failing) _failing.ExceptWith(kinds); }

    T Result<T>(string kind, T value)
    {
        lock (Calls) Calls[kind] = Calls.GetValueOrDefault(kind) + 1;
        lock (_failing) if (_failing.Contains(kind)) throw new AzureError(FailureMessage);
        return value;
    }

    IReadOnlyList<T> List<T>(string kind, params T[] items) => Result<IReadOnlyList<T>>(kind, EmptyLists ? [] : items);

    public Task<SubscriptionInfo> GetSubscriptionAsync(CancellationToken ct) => Task.FromResult(Result("subscription",
        new SubscriptionInfo("00000000-0000-0000-0000-000000000001", "Demo Subscription", "Enabled",
            "11111111-1111-1111-1111-111111111111", "AZURE_SUBSCRIPTION_ID", null)));

    public Task<IReadOnlyList<ResourceGroupInfo>> ListResourceGroupsAsync(CancellationToken ct) => Task.FromResult(List("resourcegroups",
        new ResourceGroupInfo("rg-demo", "eastus", "Succeeded"),
        new ResourceGroupInfo("MC_rg-demo_aks-demo_eastus", "eastus", "Succeeded")));

    public Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct) => Task.FromResult(List("vms",
        new VmInfo("VM", "vm-jump", "rg-demo", "eastus", "1", "Standard_B2s", "running", null),
        new VmInfo("VMSS", "aks-nodepool1-12345678-vmss", "MC_rg-demo_aks-demo_eastus", "eastus", "1, 2, 3", "Standard_D4s_v5", null, 3)));

    public Task<IReadOnlyList<StorageAccountInfo>> ListStorageAccountsAsync(CancellationToken ct) => Task.FromResult(List("storageaccounts",
        new StorageAccountInfo("stdemo001", "rg-demo", "eastus", "StorageV2", "Standard_LRS", "Hot", "Enabled")));

    public Task<IReadOnlyList<VnetInfo>> ListVnetsAsync(CancellationToken ct) => Task.FromResult(List("vnets",
        new VnetInfo("vnet-demo", "rg-demo", "eastus", "10.0.0.0/16", 2)));

    public Task<IReadOnlyList<SubnetInfo>> ListSubnetsAsync(CancellationToken ct) => Task.FromResult(List("subnets",
        new SubnetInfo("vnet-demo", "default", "10.0.0.0/24", "nsg-demo", null),
        new SubnetInfo("vnet-demo", "aks", "10.0.1.0/24", null, "rt-aks")));

    public Task<IReadOnlyList<NsgRuleInfo>> ListNsgRulesAsync(CancellationToken ct) => Task.FromResult(List("nsgrules",
        new NsgRuleInfo("nsg-demo", "allow-https", "Inbound", 100, "Allow", "Tcp", "443", "*", "10.0.0.0/24")));

    public Task<IReadOnlyList<ResourceTypeCount>> ResourceGraphSummaryAsync(CancellationToken ct) => Task.FromResult(List("resourcegraph",
        new ResourceTypeCount("microsoft.compute/virtualmachines", 1),
        new ResourceTypeCount("microsoft.network/virtualnetworks", 1)));
}
```

Replace `tests/AzureDash.Tests/Support/AppFactory.cs` with:
```csharp
using AzureDash.Configuration;
using AzureDash.Inventory;
using AzureDash.Load;
using AzureDash.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureDash.Tests.Support;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public AppSettings Settings { get; init; } = new() { ImdsEnabled = false };
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public FakeProcessExit Exit { get; } = new();
    public FakeAzureProvider Azure { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppSettings>();
            services.AddSingleton(Settings);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IProcessExit>();
            services.AddSingleton<IProcessExit>(Exit);
            services.RemoveAll<CpuLoad>();
            services.AddSingleton(_ => new CpuLoad(maxWorkers: 2));
            services.RemoveAll<Func<IAzureProvider>>();
            services.AddSingleton<Func<IAzureProvider>>(_ => () => Azure);
        });
    }
}
```

- [ ] **Step 3: Write the failing tests**

`tests/AzureDash.Tests/AzureServiceTests.cs`:
```csharp
using AzureDash.Inventory;
using AzureDash.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureDash.Tests;

public class AzureServiceTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    AzureService Service(Func<IAzureProvider> factory) =>
        new(factory, new TtlCache(TimeSpan.FromSeconds(60), _time), NullLogger<AzureService>.Instance);

    [Fact]
    public async Task Provider_is_created_lazily_and_retried_after_failure()
    {
        var created = 0;
        var fake = new FakeAzureProvider();
        var service = Service(() => ++created == 1 ? throw new InvalidOperationException("AUTH_MODE workload-identity requires AZURE_CLIENT_ID") : fake);
        Assert.Equal(0, created);

        var first = await service.FetchAsync("vms", force: false, CancellationToken.None);
        Assert.Null(first.Value);
        Assert.Equal("InvalidOperationException: AUTH_MODE workload-identity requires AZURE_CLIENT_ID", first.Error);

        var second = await service.FetchAsync("vms", force: true, CancellationToken.None);
        Assert.Null(second.Error);
        Assert.Equal(2, ((IReadOnlyList<VmInfo>)second.Value!).Count);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task Every_kind_maps_to_a_provider_call()
    {
        var fake = new FakeAzureProvider();
        var service = Service(() => fake);
        foreach (var (kind, _) in AzureKinds.All)
        {
            var entry = await service.FetchAsync(kind, false, CancellationToken.None);
            Assert.Null(entry.Error);
            Assert.NotNull(entry.Value);
            Assert.Equal(1, fake.Calls[kind]);
        }
    }

    [Fact]
    public async Task Provider_failure_is_captured_not_thrown()
    {
        var fake = new FakeAzureProvider();
        fake.Fail("storageaccounts");
        var entry = await Service(() => fake).FetchAsync("storageaccounts", false, CancellationToken.None);
        Assert.StartsWith("RequestFailedException: AuthorizationFailed (403)", entry.Error);
    }
}
```

`tests/AzureDash.Tests/AzureApiTests.cs`:
```csharp
using System.Net;
using System.Text.RegularExpressions;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class AzureApiTests
{
    [Fact]
    public async Task Api_returns_items_and_metadata()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/azure/vms")).JsonAsync();
        Assert.Equal("vms", json.GetProperty("kind").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("error").ValueKind);
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal("aks-nodepool1-12345678-vmss", json.GetProperty("items")[1].GetProperty("name").GetString());
        Assert.StartsWith("2026-01-01T00:00:00", json.GetProperty("fetched_at").GetString());
    }

    [Fact]
    public async Task Subscription_items_is_an_object()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/azure/subscription")).JsonAsync();
        Assert.Equal("Demo Subscription", json.GetProperty("items").GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Unknown_kind_is_404()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/azure/buckets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/partials/azure/buckets")).StatusCode);
    }

    [Fact]
    public async Task Failing_kind_reports_error_and_other_kinds_still_work()
    {
        using var f = new AppFactory();
        f.Azure.Fail("storageaccounts");
        var client = f.CreateClient();
        var bad = await (await client.GetAsync("/api/azure/storageaccounts")).JsonAsync();
        Assert.Contains("AuthorizationFailed", bad.GetProperty("error").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bad.GetProperty("items").ValueKind);
        var good = await (await client.GetAsync("/api/azure/vnets")).JsonAsync();
        Assert.Equal(1, good.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Cache_refresh_and_invalidate()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        await client.GetAsync("/api/azure/vnets");
        await client.GetAsync("/api/azure/vnets");
        Assert.Equal(1, f.Azure.Calls["vnets"]);
        await client.GetAsync("/api/azure/vnets?refresh=true");
        Assert.Equal(2, f.Azure.Calls["vnets"]);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/azure/refresh", null)).StatusCode);
        await client.GetAsync("/api/azure/vnets");
        Assert.Equal(3, f.Azure.Calls["vnets"]);
    }

    [Fact]
    public async Task Partial_renders_table_fragment()
    {
        using var f = new AppFactory();
        var html = await f.CreateClient().GetStringAsync("/partials/azure/storageaccounts");
        Assert.Contains("<h2>Storage accounts</h2>", html);
        Assert.Contains("stdemo001", html);
        Assert.Contains("Fetched 2026-01-01 00:00:00 UTC", html);
        Assert.Contains("hx-get=\"/partials/azure/storageaccounts?refresh=1\"", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Partial_shows_error_banner()
    {
        using var f = new AppFactory();
        f.Azure.Fail("nsgrules");
        var html = await f.CreateClient().GetStringAsync("/partials/azure/nsgrules");
        Assert.Contains("class=\"error\"", html);
        Assert.Contains("AuthorizationFailed", html);
    }

    [Fact]
    public async Task Partial_shows_stale_data_after_failed_refresh()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        await client.GetStringAsync("/partials/azure/vms");
        f.Azure.Fail("vms");
        var html = await client.GetStringAsync("/partials/azure/vms?refresh=1");
        Assert.Contains("showing previously cached data", html);
        Assert.Contains("vm-jump", html);
        Assert.Contains("class=\"error\"", html);
    }

    [Theory]
    [InlineData("resourcegroups", "No resource groups")]
    [InlineData("vms", "No virtual machines or scale sets")]
    [InlineData("storageaccounts", "No storage accounts")]
    [InlineData("vnets", "No virtual networks")]
    [InlineData("subnets", "No subnets")]
    [InlineData("nsgrules", "No custom NSG rules")]
    [InlineData("resourcegraph", "No resources")]
    public async Task Empty_lists_say_so(string kind, string message)
    {
        using var f = new AppFactory();
        f.Azure.EmptyLists = true;
        Assert.Contains(message, await f.CreateClient().GetStringAsync($"/partials/azure/{kind}"));
    }

    [Fact]
    public async Task Every_kind_renders()
    {
        using var f = new AppFactory();
        var client = f.CreateClient();
        Assert.Contains("Demo Subscription", await client.GetStringAsync("/partials/azure/subscription"));
        Assert.Contains("MC_rg-demo_aks-demo_eastus", await client.GetStringAsync("/partials/azure/resourcegroups"));
        Assert.Contains("3 instances", await client.GetStringAsync("/partials/azure/vms"));
        Assert.Contains("10.0.0.0/16", await client.GetStringAsync("/partials/azure/vnets"));
        Assert.Contains("rt-aks", await client.GetStringAsync("/partials/azure/subnets"));
        Assert.Contains("allow-https", await client.GetStringAsync("/partials/azure/nsgrules"));
        Assert.Contains("microsoft.compute/virtualmachines", await client.GetStringAsync("/partials/azure/resourcegraph"));
    }

    [Fact]
    public async Task Azure_page_has_one_lazy_panel_per_kind()
    {
        using var f = new AppFactory { Settings = new() { ImdsEnabled = false, SubscriptionId = "sub-123", ResourceGroup = "rg-x" } };
        var html = await f.CreateClient().GetStringAsync("/azure");
        Assert.Equal(8, Regex.Matches(html, "data-azure-panel").Count);
        Assert.Contains("hx-post=\"/api/azure/refresh\"", html);
        Assert.Contains("sub-123", html);
        Assert.Contains("rg-x", html);
        Assert.Empty(f.Azure.Calls);
    }
}
```

- [ ] **Step 4: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~AzureServiceTests|FullyQualifiedName~AzureApiTests"`
Expected: a build error, because `AzureService` doesn't exist yet.

- [ ] **Step 5: Implement `AzureService`**

`src/AzureDash/Inventory/AzureService.cs`:
```csharp
using System.Collections;
using System.Diagnostics;

namespace AzureDash.Inventory;

public sealed class AzureService(Func<IAzureProvider> createProvider, TtlCache cache, ILogger<AzureService> log)
{
    private readonly object _gate = new();
    private IAzureProvider? _provider;

    public Task<CacheEntry> FetchAsync(string kind, bool force, CancellationToken ct) =>
        cache.GetAsync(kind, c => LoadAsync(kind, c), force, ct);

    public void InvalidateAll() => cache.InvalidateAll();

    private IAzureProvider Provider()
    {
        lock (_gate) return _provider ??= createProvider();
    }

    private async Task<object> LoadAsync(string kind, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var p = Provider();
            object result = kind switch
            {
                "subscription" => await p.GetSubscriptionAsync(ct),
                "resourcegroups" => await p.ListResourceGroupsAsync(ct),
                "vms" => await p.ListVmsAsync(ct),
                "storageaccounts" => await p.ListStorageAccountsAsync(ct),
                "vnets" => await p.ListVnetsAsync(ct),
                "subnets" => await p.ListSubnetsAsync(ct),
                "nsgrules" => await p.ListNsgRulesAsync(ct),
                "resourcegraph" => await p.ResourceGraphSummaryAsync(ct),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown Azure kind"),
            };
            log.LogInformation("azure fetch ok kind={Kind} items={Items} in {ElapsedMs} ms",
                kind, result is ICollection c ? c.Count : 1, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = AzureError.From(ex);
            log.LogWarning("azure fetch failed kind={Kind} in {ElapsedMs} ms: {Error}", kind, sw.ElapsedMilliseconds, error.Message);
            log.LogDebug(ex, "azure fetch failure detail kind={Kind}", kind);
            throw error;
        }
    }
}
```

- [ ] **Step 6: Implement the endpoints**

`src/AzureDash/Endpoints/AzureEndpoints.cs`:
```csharp
using AzureDash.Components.Partials;
using AzureDash.Configuration;
using AzureDash.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public sealed record AzureApiResponse(string Kind, DateTimeOffset? FetchedAt, string? Error, object? Items);

public static class AzureEndpoints
{
    public static IEndpointRouteBuilder MapAzureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/azure/{kind}", Api);
        app.MapPost("/api/azure/refresh", (AzureService azure) =>
        {
            azure.InvalidateAll();
            return Results.NoContent();
        });
        app.MapGet("/partials/azure/{kind}", Partial);
        return app;
    }

    static async Task<IResult> Api(string kind, HttpRequest request, AzureService azure, CancellationToken ct)
    {
        if (!AzureKinds.IsKnown(kind)) return Results.Json(new ErrorBody($"unknown kind '{kind}'"), statusCode: 404);
        var entry = await azure.FetchAsync(kind, IsRefresh(request), ct);
        return Results.Json(new AzureApiResponse(kind, entry.FetchedAt, entry.Error, entry.Value));
    }

    static async Task<IResult> Partial(string kind, HttpRequest request, AzureService azure, CancellationToken ct)
    {
        if (!AzureKinds.IsKnown(kind)) return Results.NotFound();
        var entry = await azure.FetchAsync(kind, IsRefresh(request), ct);
        return new RazorComponentResult<AzurePanel>(new { Kind = kind, Entry = entry });
    }

    static bool IsRefresh(HttpRequest request) => AppSettings.TryParseBool(request.Query["refresh"].ToString()) == true;
}
```

- [ ] **Step 7: Write the panel and table components**

`src/AzureDash/Components/Partials/AzurePanel.razor`:
```razor
@using AzureDash.Inventory
@using AzureDash.Components.Partials.Inventory
<h2>@AzureKinds.Title(Kind)</h2>
@if (Entry.Error is not null)
{
    <div class="error">@Entry.Error</div>
}
@switch (Entry.Value)
{
    case SubscriptionInfo s:
        <SubscriptionTable Item="s" />
        break;
    case IReadOnlyList<ResourceGroupInfo> l:
        <ResourceGroupsTable Items="l" />
        break;
    case IReadOnlyList<VmInfo> l:
        <VmsTable Items="l" />
        break;
    case IReadOnlyList<StorageAccountInfo> l:
        <StorageAccountsTable Items="l" />
        break;
    case IReadOnlyList<VnetInfo> l:
        <VnetsTable Items="l" />
        break;
    case IReadOnlyList<SubnetInfo> l:
        <SubnetsTable Items="l" />
        break;
    case IReadOnlyList<NsgRuleInfo> l:
        <NsgRulesTable Items="l" />
        break;
    case IReadOnlyList<ResourceTypeCount> l:
        <ResourceGraphTable Items="l" />
        break;
}
<p class="muted">
    @if (Entry.Error is not null && Entry.Value is not null)
    {
        <text>Last attempt @Fmt.Time(Entry.FetchedAt) failed; showing previously cached data</text>
    }
    else if (Entry.FetchedAt is not null)
    {
        <text>Fetched @Fmt.Time(Entry.FetchedAt)</text>
    }
    else
    {
        <text>Never fetched</text>
    }
    · <a href="#" hx-get="/partials/azure/@(Kind)?refresh=1" hx-target="closest [data-azure-panel]">refresh</a>
</p>

@code {
    [Parameter, EditorRequired] public string Kind { get; set; } = "";
    [Parameter, EditorRequired] public CacheEntry Entry { get; set; } = default!;
}
```

`src/AzureDash/Components/Partials/Inventory/SubscriptionTable.razor`:
```razor
@using AzureDash.Inventory
@if (Item.Warning is not null)
{
    <div class="warning">@Item.Warning</div>
}
<dl class="kv">
    <dt>Subscription ID</dt><dd>@Item.Id</dd>
    <dt>Name</dt><dd>@Item.DisplayName</dd>
    <dt>State</dt><dd>@Fmt.Or(Item.State)</dd>
    <dt>Tenant ID</dt><dd>@Fmt.Or(Item.TenantId)</dd>
    <dt>Selected by</dt><dd>@Item.Source</dd>
</dl>

@code {
    [Parameter, EditorRequired] public SubscriptionInfo Item { get; set; } = default!;
}
```

`src/AzureDash/Components/Partials/Inventory/ResourceGroupsTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No resource groups.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>Name</th><th>Location</th><th>Provisioning</th></tr></thead>
            <tbody>
                @foreach (var g in Items)
                {
                    <tr><td>@g.Name</td><td>@g.Location</td><td>@Fmt.Or(g.ProvisioningState)</td></tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<ResourceGroupInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/VmsTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No virtual machines or scale sets.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>Kind</th><th>Name</th><th>Resource group</th><th>Location</th><th>Zones</th><th>Size / SKU</th><th>Power / capacity</th></tr></thead>
            <tbody>
                @foreach (var v in Items)
                {
                    <tr>
                        <td>@v.Kind</td><td>@v.Name</td><td>@v.ResourceGroup</td><td>@v.Location</td>
                        <td>@Fmt.Or(v.Zones)</td><td>@Fmt.Or(v.Size)</td>
                        <td>@(v.Kind == "VMSS" ? $"{v.Capacity?.ToString() ?? "?"} instances" : Fmt.Or(v.PowerState))</td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<VmInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/StorageAccountsTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No storage accounts.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>Name</th><th>Resource group</th><th>Location</th><th>Kind</th><th>SKU</th><th>Access tier</th><th>Public network access</th></tr></thead>
            <tbody>
                @foreach (var a in Items)
                {
                    <tr>
                        <td>@a.Name</td><td>@a.ResourceGroup</td><td>@a.Location</td><td>@Fmt.Or(a.Kind)</td>
                        <td>@Fmt.Or(a.Sku)</td><td>@Fmt.Or(a.AccessTier)</td><td>@Fmt.Or(a.PublicNetworkAccess)</td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<StorageAccountInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/VnetsTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No virtual networks.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>Name</th><th>Resource group</th><th>Location</th><th>Address space</th><th>Subnets</th></tr></thead>
            <tbody>
                @foreach (var n in Items)
                {
                    <tr><td>@n.Name</td><td>@n.ResourceGroup</td><td>@n.Location</td><td>@n.AddressSpace</td><td>@n.SubnetCount</td></tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<VnetInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/SubnetsTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No subnets.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>VNet</th><th>Name</th><th>Prefix</th><th>NSG</th><th>Route table</th></tr></thead>
            <tbody>
                @foreach (var s in Items)
                {
                    <tr><td>@s.Vnet</td><td>@s.Name</td><td>@Fmt.Or(s.AddressPrefix)</td><td>@Fmt.Or(s.Nsg)</td><td>@Fmt.Or(s.RouteTable)</td></tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<SubnetInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/NsgRulesTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No custom NSG rules (default rules are not shown).</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>NSG</th><th>Rule</th><th>Direction</th><th>Priority</th><th>Access</th><th>Protocol</th><th>Ports</th><th>Source → destination</th></tr></thead>
            <tbody>
                @foreach (var r in Items)
                {
                    <tr>
                        <td>@r.Nsg</td><td>@r.Rule</td><td>@Fmt.Or(r.Direction)</td><td>@(r.Priority?.ToString() ?? "—")</td>
                        <td>@Fmt.Or(r.Access)</td><td>@Fmt.Or(r.Protocol)</td><td>@r.Ports</td><td>@r.Source → @r.Destination</td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
    <p class="muted">Default rules are not shown.</p>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<NsgRuleInfo> Items { get; set; } = [];
}
```

`src/AzureDash/Components/Partials/Inventory/ResourceGraphTable.razor`:
```razor
@using AzureDash.Inventory
@if (Items.Count == 0)
{
    <p class="muted">No resources visible to this identity.</p>
}
else
{
    <div class="table-wrap">
        <table>
            <thead><tr><th>Resource type</th><th>Count</th></tr></thead>
            <tbody>
                @foreach (var t in Items)
                {
                    <tr><td>@t.Type</td><td>@t.Count</td></tr>
                }
            </tbody>
        </table>
    </div>
    <p class="muted">One KQL query: <code>Resources | summarize count() by type</code>, limited to what this identity can read.</p>
}

@code {
    [Parameter, EditorRequired] public IReadOnlyList<ResourceTypeCount> Items { get; set; } = [];
}
```

- [ ] **Step 8: Write the Azure page**

`src/AzureDash/Pages/Azure.cshtml`:
```cshtml
@page "/azure"
@using AzureDash.Configuration
@using AzureDash.Inventory
@inject AppSettings Settings
@{
    ViewData["Title"] = "Azure";
    ViewData["Nav"] = "azure";
}
<div class="grid">
    <section class="card">
        <div class="header-row">
            <h2>Azure resources</h2>
            <button class="secondary" hx-post="/api/azure/refresh" hx-swap="none"
                    hx-on::after-request="document.querySelectorAll('[data-azure-panel]').forEach(p => htmx.trigger(p, 'refresh'))">Refresh all</button>
        </div>
        <dl class="kv">
            <dt>Subscription</dt><dd>@(Settings.SubscriptionId ?? "auto-detect (AZURE_SUBSCRIPTION_ID not set)")</dd>
            <dt>Resource group</dt><dd>@(Settings.ResourceGroup ?? "all (AZURE_RESOURCE_GROUP not set)")</dd>
            <dt>Cloud</dt><dd>@Settings.Cloud</dd>
        </dl>
        <p class="muted">Results are cached for @Settings.CacheTtl.TotalSeconds seconds. Each panel loads and fails independently.</p>
    </section>
    @foreach (var (kind, title) in AzureKinds.All)
    {
        <section class="card" data-azure-panel hx-get="/partials/azure/@kind" hx-trigger="load, refresh">
            <h2>@title</h2>
            <p class="muted">Loading…</p>
        </section>
    }
</div>
```

- [ ] **Step 9: Wire up DI and routes**

In `src/AzureDash/Program.cs`:
- add `using AzureDash.Inventory;`
- after the `RuntimeInfoProvider` registration, add:
  ```csharp
  builder.Services.AddSingleton(sp => new TtlCache(sp.GetRequiredService<AppSettings>().CacheTtl, sp.GetRequiredService<TimeProvider>()));
  builder.Services.AddSingleton<Func<IAzureProvider>>(_ => () => throw new AzureError("no Azure provider is registered"));
  builder.Services.AddSingleton<AzureService>();
  ```
- after `app.MapRuntimeEndpoints();`, add:
  ```csharp
  app.MapAzureEndpoints();
  ```

- [ ] **Step 10: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: add Azure inventory service, API, and htmx panels"
```

---

### Task 7: Credential selection, SPIFFE assertion source, JWT display

**Files:**
- Create: `src/AzureDash/Identity/{CloudEndpoints,CredentialFactory,CredentialProvider,SpiffeAssertionSource,JwtDisplay}.cs`
- Modify: `src/AzureDash/Program.cs`
- Create: `tests/AzureDash.Tests/Support/TestJwt.cs`
- Test: `tests/AzureDash.Tests/CredentialFactoryTests.cs`, `tests/AzureDash.Tests/SpiffeAssertionSourceTests.cs`, `tests/AzureDash.Tests/JwtDisplayTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `AuthMode`, `AuthModes`, `AzureCloud`, `EnvLookup` (Task 1).
- Produces:
  - `record CloudEndpoints(Uri AuthorityHost, ArmEnvironment Arm, string ArmScope)`, with `CloudEndpoints.For(AzureCloud)`
  - `record CredentialSelection(AuthMode Mode, string Reason, TokenCredential Credential, string CredentialType, string? ClientId, string? TenantId, Uri AuthorityHost, string? TokenFile)`
  - `CredentialFactory.Resolve(AppSettings, EnvLookup) → (AuthMode Mode, string Reason)`. It never throws.
  - `CredentialFactory.Create(AppSettings, EnvLookup) → CredentialSelection`. It throws `InvalidOperationException("AUTH_MODE <mode> requires <VARS>")` when required values are missing.
  - `CredentialProvider(Func<CredentialSelection> create, Func<(AuthMode Mode, string Reason)> describe)`:
    - `CredentialProvider.FromSettings(AppSettings, EnvLookup)`
    - `Get()` caches a successful selection only; a failure is retried on the next call
    - `Describe()`
  - `SpiffeAssertionSource(string path)`:
    - `ReadAsync(CancellationToken) → Task<string>` re-reads the file on every call
    - `static Normalize(string raw, string path) → string` accepts a raw JWT or a base64-wrapped JWT
  - `record JwtSummary(IReadOnlyDictionary<string,string> Claims, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, string? Error)`, with `JwtSummary.Failed(string)`
  - `JwtDisplay.Decode(string? token, IReadOnlyList<string> claims) → JwtSummary`. It never throws.
  - Claim lists: `JwtDisplay.FederatedClaims = [iss, sub, aud, iat, exp]` and `JwtDisplay.AccessTokenClaims = [aud, iss, oid, tid, appid, azp, idtyp, ver, xms_mirid, exp]`
  - Test helper: `TestJwt.Make(object payload) → string`, whose signature segment is `c2lnbmF0dXJl`

- [ ] **Step 1: Write the test helper and the failing tests**

`tests/AzureDash.Tests/Support/TestJwt.cs`:
```csharp
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace AzureDash.Tests.Support;

public static class TestJwt
{
    public const string Signature = "c2lnbmF0dXJl";

    public static string Make(object payload) =>
        $"{B64("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{B64(JsonSerializer.Serialize(payload))}.{Signature}";

    static string B64(string s) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(s));
}
```

`tests/AzureDash.Tests/CredentialFactoryTests.cs`:
```csharp
using Azure.Identity;
using AzureDash.Configuration;
using AzureDash.Identity;

namespace AzureDash.Tests;

public class CredentialFactoryTests
{
    static EnvLookup Env(params (string Key, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return name => d.GetValueOrDefault(name);
    }

    static readonly (string, string)[] Ids = [("AZURE_CLIENT_ID", "cid"), ("AZURE_TENANT_ID", "tid")];

    [Fact]
    public void Explicit_mode_wins()
    {
        var (mode, reason) = CredentialFactory.Resolve(new AppSettings { AuthMode = AuthMode.ClientSecret },
            Env(("AZURE_FEDERATED_TOKEN_FILE", "/t")));
        Assert.Equal(AuthMode.ClientSecret, mode);
        Assert.Equal("AUTH_MODE=client-secret", reason);
    }

    [Fact]
    public void Auto_prefers_workload_identity_then_spiffe_then_secret_then_dev()
    {
        var all = new AppSettings { SpiffeJwtFile = "/s" };
        Assert.Equal(AuthMode.WorkloadIdentity, CredentialFactory.Resolve(all, Env(("AZURE_FEDERATED_TOKEN_FILE", "/t"), ("AZURE_CLIENT_SECRET", "x"))).Mode);
        Assert.Equal(AuthMode.Spiffe, CredentialFactory.Resolve(all, Env(("AZURE_CLIENT_SECRET", "x"))).Mode);
        Assert.Equal(AuthMode.ClientSecret, CredentialFactory.Resolve(new AppSettings(), Env(("AZURE_CLIENT_SECRET", "x"))).Mode);
        var (dev, reason) = CredentialFactory.Resolve(new AppSettings(), Env());
        Assert.Equal(AuthMode.Dev, dev);
        Assert.StartsWith("auto:", reason);
    }

    [Fact]
    public void Workload_identity_selection()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env([.. Ids, ("AZURE_FEDERATED_TOKEN_FILE", "/var/run/secrets/azure/tokens/azure-identity-token")]));
        Assert.Equal(AuthMode.WorkloadIdentity, s.Mode);
        Assert.IsType<WorkloadIdentityCredential>(s.Credential);
        Assert.Equal("WorkloadIdentityCredential", s.CredentialType);
        Assert.Equal("cid", s.ClientId);
        Assert.Equal("tid", s.TenantId);
        Assert.Equal("/var/run/secrets/azure/tokens/azure-identity-token", s.TokenFile);
        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud, s.AuthorityHost);
    }

    [Fact]
    public void Workload_identity_missing_values_names_them()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialFactory.Create(
            new AppSettings { AuthMode = AuthMode.WorkloadIdentity }, Env(("AZURE_CLIENT_ID", "cid"))));
        Assert.Equal("AUTH_MODE workload-identity requires AZURE_TENANT_ID, AZURE_FEDERATED_TOKEN_FILE", ex.Message);
    }

    [Fact]
    public void Spiffe_selection_uses_client_assertion()
    {
        var s = CredentialFactory.Create(new AppSettings { SpiffeJwtFile = "/var/run/secrets/azure/token" }, Env(Ids));
        Assert.Equal(AuthMode.Spiffe, s.Mode);
        Assert.IsType<ClientAssertionCredential>(s.Credential);
        Assert.Equal("/var/run/secrets/azure/token", s.TokenFile);
    }

    [Fact]
    public void Client_secret_selection()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env([.. Ids, ("AZURE_CLIENT_SECRET", "shh")]));
        Assert.IsType<ClientSecretCredential>(s.Credential);
        Assert.Null(s.TokenFile);
    }

    [Fact]
    public void Dev_selection_is_a_chain()
    {
        var s = CredentialFactory.Create(new AppSettings(), Env());
        Assert.Equal(AuthMode.Dev, s.Mode);
        Assert.IsType<ChainedTokenCredential>(s.Credential);
    }

    [Fact]
    public void Authority_host_env_overrides_cloud_default()
    {
        Assert.Equal(AzureAuthorityHosts.AzureGovernment,
            CredentialFactory.Create(new AppSettings { Cloud = AzureCloud.UsGov }, Env()).AuthorityHost);
        Assert.Equal(new Uri("https://login.example.test/"),
            CredentialFactory.Create(new AppSettings(), Env(("AZURE_AUTHORITY_HOST", "https://login.example.test/"))).AuthorityHost);
    }

    [Fact]
    public void Cloud_endpoints()
    {
        Assert.Equal("https://management.azure.com/.default", CloudEndpoints.For(AzureCloud.Public).ArmScope);
        Assert.Equal("https://management.usgovcloudapi.net/.default", CloudEndpoints.For(AzureCloud.UsGov).ArmScope);
        Assert.Equal("https://management.chinacloudapi.cn/.default", CloudEndpoints.For(AzureCloud.China).ArmScope);
    }

    [Fact]
    public void Provider_caches_success_and_retries_failure()
    {
        var calls = 0;
        var ok = CredentialFactory.Create(new AppSettings(), Env());
        var provider = new CredentialProvider(() => ++calls == 1 ? throw new InvalidOperationException("not yet") : ok, () => (AuthMode.Dev, "test"));
        Assert.Throws<InvalidOperationException>(provider.Get);
        Assert.Same(ok, provider.Get());
        Assert.Same(ok, provider.Get());
        Assert.Equal(2, calls);
        Assert.Equal((AuthMode.Dev, "test"), provider.Describe());
    }
}
```

`tests/AzureDash.Tests/SpiffeAssertionSourceTests.cs`:
```csharp
using System.Text;
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class SpiffeAssertionSourceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffe-").FullName;
    string PathOf(string name) => Path.Combine(_dir, name);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Reads_raw_jwt_and_trims_whitespace()
    {
        var jwt = TestJwt.Make(new { sub = "spiffe://td/ns/azure-dash-ztwim/sa/azure-dash" });
        await File.WriteAllTextAsync(PathOf("token"), jwt + "\n");
        Assert.Equal(jwt, await new SpiffeAssertionSource(PathOf("token")).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unwraps_base64_encoded_jwt()
    {
        var jwt = TestJwt.Make(new { sub = "x" });
        await File.WriteAllTextAsync(PathOf("token"), Convert.ToBase64String(Encoding.UTF8.GetBytes(jwt)));
        Assert.Equal(jwt, await new SpiffeAssertionSource(PathOf("token")).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Re_reads_the_file_every_call()
    {
        var source = new SpiffeAssertionSource(PathOf("token"));
        var first = TestJwt.Make(new { n = 1 });
        var second = TestJwt.Make(new { n = 2 });
        await File.WriteAllTextAsync(PathOf("token"), first);
        Assert.Equal(first, await source.ReadAsync(CancellationToken.None));
        await File.WriteAllTextAsync(PathOf("token"), second);
        Assert.Equal(second, await source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_file_names_path_and_sidecar()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new SpiffeAssertionSource(PathOf("absent")).ReadAsync(CancellationToken.None));
        Assert.Contains(PathOf("absent"), ex.Message);
        Assert.Contains("spiffe-helper", ex.Message);
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("   \n", "is empty")]
    [InlineData("not a token", "does not contain a JWT")]
    [InlineData("aGVsbG8gd29ybGQ=", "does not contain a JWT")]
    public void Rejects_non_jwt_content(string content, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SpiffeAssertionSource.Normalize(content, "/p"));
        Assert.Contains(message, ex.Message);
        Assert.Contains("/p", ex.Message);
    }
}
```

`tests/AzureDash.Tests/JwtDisplayTests.cs`:
```csharp
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class JwtDisplayTests
{
    [Fact]
    public void Decodes_selected_claims_in_order_and_times()
    {
        var jwt = TestJwt.Make(new
        {
            iss = "https://eastus.oic.prod-aks.azure.com/t/u/",
            sub = "system:serviceaccount:azure-dash:azure-dash",
            aud = new[] { "api://AzureADTokenExchange" },
            iat = 1767225600,
            exp = 1767229200,
            secret_claim = "not requested",
        });
        var s = JwtDisplay.Decode(jwt, JwtDisplay.FederatedClaims);
        Assert.Null(s.Error);
        Assert.Equal(new[] { "iss", "sub", "aud", "iat", "exp" }, s.Claims.Keys);
        Assert.Equal("system:serviceaccount:azure-dash:azure-dash", s.Claims["sub"]);
        Assert.Equal("api://AzureADTokenExchange", s.Claims["aud"]);
        Assert.Equal("1767229200", s.Claims["exp"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), s.IssuedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767229200), s.ExpiresAt);
        Assert.DoesNotContain(s.Claims.Values, v => v.Contains(TestJwt.Signature));
    }

    [Fact]
    public void Absent_claims_are_skipped()
    {
        var s = JwtDisplay.Decode(TestJwt.Make(new { oid = "o", tid = "t", ver = "1.0", appid = "a" }), JwtDisplay.AccessTokenClaims);
        Assert.Equal(new[] { "oid", "tid", "appid", "ver" }, s.Claims.Keys);
        Assert.Null(s.ExpiresAt);
    }

    [Theory]
    [InlineData(null, "no token")]
    [InlineData("", "no token")]
    [InlineData("abc", "expected 3 dot-separated segments")]
    [InlineData("a.!!!.c", "could not decode token")]
    [InlineData("a.bm90IGpzb24.c", "could not decode token")]
    [InlineData("a.WzEsMl0.c", "payload is not a JSON object")]
    public void Malformed_tokens_return_error_not_exception(string? token, string message)
    {
        var s = JwtDisplay.Decode(token, JwtDisplay.FederatedClaims);
        Assert.Contains(message, s.Error);
        Assert.Empty(s.Claims);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~CredentialFactoryTests|FullyQualifiedName~SpiffeAssertionSourceTests|FullyQualifiedName~JwtDisplayTests"`
Expected: a build error, because the `AzureDash.Identity` types don't exist yet.

- [ ] **Step 3: Implement `CloudEndpoints`, `SpiffeAssertionSource` and `JwtDisplay`**

`src/AzureDash/Identity/CloudEndpoints.cs`:
```csharp
using Azure.Identity;
using Azure.ResourceManager;
using AzureDash.Configuration;

namespace AzureDash.Identity;

public sealed record CloudEndpoints(Uri AuthorityHost, ArmEnvironment Arm, string ArmScope)
{
    public static CloudEndpoints For(AzureCloud cloud) => cloud switch
    {
        AzureCloud.UsGov => new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment, "https://management.usgovcloudapi.net/.default"),
        AzureCloud.China => new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina, "https://management.chinacloudapi.cn/.default"),
        _ => new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud, "https://management.azure.com/.default"),
    };
}
```

`src/AzureDash/Identity/SpiffeAssertionSource.cs`:
```csharp
using System.Text;

namespace AzureDash.Identity;

/// <summary>
/// Supplies the SPIFFE JWT-SVID written by spiffe-helper as the client assertion. Re-reads the file on every
/// token exchange because ZTWIM JWT-SVIDs can live only ~5 minutes.
/// </summary>
public sealed class SpiffeAssertionSource(string path)
{
    public async Task<string> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"SPIFFE JWT-SVID file not found at {path} (is the spiffe-helper sidecar running?)", path);
        return Normalize(await File.ReadAllTextAsync(path, ct), path);
    }

    public static string Normalize(string raw, string path)
    {
        var text = raw.Trim();
        if (text.Length == 0) throw new InvalidOperationException($"SPIFFE JWT-SVID file {path} is empty");
        if (LooksLikeJwt(text)) return text;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            if (LooksLikeJwt(decoded)) return decoded;
        }
        catch (FormatException)
        {
        }
        throw new InvalidOperationException($"SPIFFE JWT-SVID file {path} does not contain a JWT");
    }

    static bool LooksLikeJwt(string s) => s.StartsWith("eyJ", StringComparison.Ordinal) && s.Count(c => c == '.') == 2;
}
```

`src/AzureDash/Identity/JwtDisplay.cs`:
```csharp
using System.Buffers.Text;
using System.Text.Json;

namespace AzureDash.Identity;

public sealed record JwtSummary(IReadOnlyDictionary<string, string> Claims, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, string? Error)
{
    public static JwtSummary Failed(string error) => new(new Dictionary<string, string>(), null, null, error);
}

/// <summary>Decodes (never verifies) a JWT payload and returns only the requested claims, for display.</summary>
public static class JwtDisplay
{
    public static readonly IReadOnlyList<string> FederatedClaims = ["iss", "sub", "aud", "iat", "exp"];
    public static readonly IReadOnlyList<string> AccessTokenClaims = ["aud", "iss", "oid", "tid", "appid", "azp", "idtyp", "ver", "xms_mirid", "exp"];

    public static JwtSummary Decode(string? token, IReadOnlyList<string> claims)
    {
        if (string.IsNullOrWhiteSpace(token)) return JwtSummary.Failed("no token");
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return JwtSummary.Failed($"could not decode token: expected 3 dot-separated segments, found {parts.Length}");
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return JwtSummary.Failed("could not decode token: payload is not a JSON object");
            var result = new Dictionary<string, string>();
            foreach (var name in claims)
                if (root.TryGetProperty(name, out var value)) result[name] = Render(value);
            return new JwtSummary(result, Epoch(root, "iat"), Epoch(root, "exp"), null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return JwtSummary.Failed($"could not decode token: {ex.Message}");
        }
    }

    static string Render(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Render)),
        _ => v.GetRawText(),
    };

    static DateTimeOffset? Epoch(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;
}
```

- [ ] **Step 4: Implement `CredentialFactory` and `CredentialProvider`**

`src/AzureDash/Identity/CredentialFactory.cs`:
```csharp
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
```

`src/AzureDash/Identity/CredentialProvider.cs`:
```csharp
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
```

In `src/AzureDash/Program.cs`:
- add `using AzureDash.Identity;`
- before the `Func<IAzureProvider>` registration, add:
  ```csharp
  builder.Services.AddSingleton(sp => CredentialProvider.FromSettings(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<EnvLookup>()));
  ```

- [ ] **Step 5: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add explicit credential selection, SPIFFE assertion source, JWT display"
```

---

### Task 8: Live Azure provider, scope resolution, SDK debug logging

**Files:**
- Create: `src/AzureDash/Inventory/{ScopeResolver,AzureMappers,LiveAzureProvider}.cs`, `src/AzureDash/AzureSdkLogging.cs`
- Modify: `src/AzureDash/Program.cs`
- Test: `tests/AzureDash.Tests/ScopeResolverTests.cs`, `tests/AzureDash.Tests/AzureMappersTests.cs`, `tests/AzureDash.Tests/LiveAzureProviderTests.cs`

**Interfaces:**
- Consumes: `IAzureProvider` and the model records (Task 6), `AzureError` (Task 5), `CredentialProvider`, `CredentialSelection`, `CloudEndpoints` (Task 7), `AppSettings`.
- Produces:
  - `record ResolvedScope(string SubscriptionId, string Source, string? Warning)`
  - `ScopeResolver.Choose(string? configured, IReadOnlyList<string> visible, string? resourceGroup) → ResolvedScope`. It throws `AzureError` when no subscription can be chosen.
  - `AzureMappers`:
    - `PowerState(IEnumerable<string?>?) → string?`
    - `Join(IEnumerable<string?>?) → string?`
    - `OneOrMany(string? single, IEnumerable<string?>? many) → string`, which returns `"*"` when both are empty
    - `KqlString(string) → string`
    - `ParseGraphRows(BinaryData) → IReadOnlyList<ResourceTypeCount>`
  - `LiveAzureProvider(CredentialProvider, AppSettings) : IAzureProvider`. The constructor calls `credentials.Get()`, so credential errors come out of the provider factory.
  - `AzureSdkLogging(ILoggerFactory)` with `Start()` and `IDisposable`. It forwards Azure SDK EventSource events to the `Azure.Sdk` logger category.
  - DI: `Func<IAzureProvider>` now builds a `LiveAzureProvider`.

- [ ] **Step 1: Write the failing tests**

`tests/AzureDash.Tests/ScopeResolverTests.cs`:
```csharp
using AzureDash.Inventory;

namespace AzureDash.Tests;

public class ScopeResolverTests
{
    [Fact]
    public void Configured_subscription_wins()
    {
        var s = ScopeResolver.Choose("sub-cfg", ["sub-a", "sub-b"], null);
        Assert.Equal(new ResolvedScope("sub-cfg", "AZURE_SUBSCRIPTION_ID", null), s);
    }

    [Fact]
    public void Single_visible_subscription_is_used()
    {
        var s = ScopeResolver.Choose(null, ["sub-a"], null);
        Assert.Equal(new ResolvedScope("sub-a", "only subscription visible to this identity", null), s);
    }

    [Fact]
    public void Several_visible_picks_first_sorted_and_warns()
    {
        var s = ScopeResolver.Choose(null, ["sub-b", "sub-a", "sub-c"], null);
        Assert.Equal("sub-a", s.SubscriptionId);
        Assert.Equal("first of 3 visible subscriptions", s.Source);
        Assert.Equal("3 subscriptions are visible to this identity; showing sub-a. Set AZURE_SUBSCRIPTION_ID to choose one.", s.Warning);
    }

    [Fact]
    public void None_visible_with_resource_group_scope_asks_for_subscription_id()
    {
        var ex = Assert.Throws<AzureError>(() => ScopeResolver.Choose(null, [], "rg-demo"));
        Assert.Contains("AZURE_RESOURCE_GROUP=rg-demo", ex.Message);
        Assert.Contains("set AZURE_SUBSCRIPTION_ID", ex.Message);
    }

    [Fact]
    public void None_visible_without_resource_group_asks_for_reader()
    {
        var ex = Assert.Throws<AzureError>(() => ScopeResolver.Choose(null, [], null));
        Assert.Contains("grant Reader", ex.Message);
        Assert.Contains("AZURE_SUBSCRIPTION_ID", ex.Message);
    }
}
```

`tests/AzureDash.Tests/AzureMappersTests.cs`:
```csharp
using AzureDash.Inventory;

namespace AzureDash.Tests;

public class AzureMappersTests
{
    [Fact]
    public void Power_state_from_instance_view_codes()
    {
        Assert.Equal("running", AzureMappers.PowerState(["ProvisioningState/succeeded", "PowerState/running"]));
        Assert.Equal("deallocated", AzureMappers.PowerState(["powerstate/deallocated"]));
        Assert.Null(AzureMappers.PowerState(["ProvisioningState/succeeded"]));
        Assert.Null(AzureMappers.PowerState(null));
    }

    [Fact]
    public void Join_skips_blanks_and_returns_null_when_empty()
    {
        Assert.Equal("1, 3", AzureMappers.Join(["1", "", null, "3"]));
        Assert.Null(AzureMappers.Join([]));
        Assert.Null(AzureMappers.Join(null));
    }

    [Fact]
    public void One_or_many_prefers_list_then_single_then_star()
    {
        Assert.Equal("80, 443", AzureMappers.OneOrMany("22", ["80", "443"]));
        Assert.Equal("22", AzureMappers.OneOrMany("22", []));
        Assert.Equal("*", AzureMappers.OneOrMany(null, null));
    }

    [Fact]
    public void Kql_string_escapes_quotes_and_backslashes()
    {
        Assert.Equal("'rg-demo'", AzureMappers.KqlString("rg-demo"));
        Assert.Equal(@"'a\'b\\c'", AzureMappers.KqlString(@"a'b\c"));
    }

    [Fact]
    public void Parses_resource_graph_object_array()
    {
        var rows = AzureMappers.ParseGraphRows(BinaryData.FromString(
            "[{\"type\":\"microsoft.compute/virtualmachines\",\"count_\":3},{\"type\":\"microsoft.network/virtualnetworks\",\"count_\":1}]"));
        Assert.Equal(new[] { new ResourceTypeCount("microsoft.compute/virtualmachines", 3), new ResourceTypeCount("microsoft.network/virtualnetworks", 1) }, rows);
    }

    [Fact]
    public void Resource_graph_non_array_is_an_error()
    {
        Assert.Throws<AzureError>(() => AzureMappers.ParseGraphRows(BinaryData.FromString("{\"columns\":[]}")));
    }
}
```

`tests/AzureDash.Tests/LiveAzureProviderTests.cs`:
```csharp
using AzureDash.Configuration;
using AzureDash.Identity;
using AzureDash.Inventory;

namespace AzureDash.Tests;

public class LiveAzureProviderTests
{
    [Fact]
    public void Construction_surfaces_credential_errors()
    {
        var creds = new CredentialProvider(() => throw new InvalidOperationException("AUTH_MODE spiffe requires AZURE_CLIENT_ID"), () => (AuthMode.Spiffe, "test"));
        var ex = Assert.Throws<InvalidOperationException>(() => new LiveAzureProvider(creds, new AppSettings()));
        Assert.Contains("AZURE_CLIENT_ID", ex.Message);
    }

    [Fact]
    public void Construction_makes_no_network_calls()
    {
        var creds = CredentialProvider.FromSettings(new AppSettings { AuthMode = AuthMode.Dev }, _ => null);
        _ = new LiveAzureProvider(creds, new AppSettings { SubscriptionId = "00000000-0000-0000-0000-000000000001", Cloud = AzureCloud.UsGov });
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~ScopeResolverTests|FullyQualifiedName~AzureMappersTests|FullyQualifiedName~LiveAzureProviderTests"`
Expected: a build error, because `ScopeResolver`, `AzureMappers` and `LiveAzureProvider` don't exist yet.

- [ ] **Step 3: Implement `ScopeResolver` and `AzureMappers`**

`src/AzureDash/Inventory/ScopeResolver.cs`:
```csharp
namespace AzureDash.Inventory;

public sealed record ResolvedScope(string SubscriptionId, string Source, string? Warning);

public static class ScopeResolver
{
    public static ResolvedScope Choose(string? configured, IReadOnlyList<string> visible, string? resourceGroup)
    {
        if (configured is not null) return new(configured, "AZURE_SUBSCRIPTION_ID", null);
        var sorted = visible.Order(StringComparer.Ordinal).ToList();
        return sorted.Count switch
        {
            0 when resourceGroup is not null => throw new AzureError(
                $"No subscriptions are visible to this identity. With AZURE_RESOURCE_GROUP={resourceGroup} (Reader scoped to a resource group), set AZURE_SUBSCRIPTION_ID too."),
            0 => throw new AzureError(
                "No subscriptions are visible to this identity: grant Reader on a subscription, or set AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP for a resource-group-scoped role."),
            1 => new(sorted[0], "only subscription visible to this identity", null),
            var n => new(sorted[0], $"first of {n} visible subscriptions",
                $"{n} subscriptions are visible to this identity; showing {sorted[0]}. Set AZURE_SUBSCRIPTION_ID to choose one."),
        };
    }
}
```

`src/AzureDash/Inventory/AzureMappers.cs`:
```csharp
using System.Text.Json;

namespace AzureDash.Inventory;

public static class AzureMappers
{
    const string PowerStatePrefix = "PowerState/";

    public static string? PowerState(IEnumerable<string?>? statusCodes) =>
        statusCodes?.FirstOrDefault(c => c?.StartsWith(PowerStatePrefix, StringComparison.OrdinalIgnoreCase) == true)?[PowerStatePrefix.Length..];

    public static string? Join(IEnumerable<string?>? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list is { Count: > 0 } ? string.Join(", ", list) : null;
    }

    public static string OneOrMany(string? single, IEnumerable<string?>? many) =>
        Join(many) ?? (string.IsNullOrWhiteSpace(single) ? "*" : single);

    public static string KqlString(string value) => "'" + value.Replace(@"\", @"\\").Replace("'", @"\'") + "'";

    public static IReadOnlyList<ResourceTypeCount> ParseGraphRows(BinaryData data)
    {
        using var doc = JsonDocument.Parse(data.ToMemory());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new AzureError("Resource Graph returned an unexpected result format (expected an object array)");
        return doc.RootElement.EnumerateArray()
            .Select(row => new ResourceTypeCount(
                row.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                row.TryGetProperty("count_", out var c) && c.TryGetInt64(out var n) ? n : 0))
            .ToList();
    }
}
```

- [ ] **Step 4: Implement `LiveAzureProvider`**

`src/AzureDash/Inventory/LiveAzureProvider.cs`:
```csharp
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using AzureDash.Configuration;
using AzureDash.Identity;

namespace AzureDash.Inventory;

/// <summary>
/// Azure Resource Manager + Resource Graph implementation. Lists at subscription scope, or at resource-group
/// scope when AZURE_RESOURCE_GROUP is set (least-privilege Reader).
/// </summary>
public sealed class LiveAzureProvider : IAzureProvider
{
    private readonly ArmClient _arm;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _scopeGate = new(1, 1);
    private ResolvedScope? _scope;

    public LiveAzureProvider(CredentialProvider credentials, AppSettings settings)
    {
        _settings = settings;
        var selection = credentials.Get();
        _arm = new ArmClient(selection.Credential, settings.SubscriptionId,
            new ArmClientOptions { Environment = CloudEndpoints.For(settings.Cloud).Arm });
    }

    public async Task<SubscriptionInfo> GetSubscriptionAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        try
        {
            var data = (await Subscription(scope).GetAsync(ct)).Value.Data;
            return new(data.SubscriptionId, data.DisplayName, data.State?.ToString(), data.TenantId?.ToString(), scope.Source, scope.Warning);
        }
        catch (RequestFailedException e) when (e.Status is 403 or 404)
        {
            return new(scope.SubscriptionId, "(details not readable at this scope)", null, null, scope.Source, scope.Warning);
        }
    }

    public async Task<IReadOnlyList<ResourceGroupInfo>> ListResourceGroupsAsync(CancellationToken ct)
    {
        var sub = Subscription(await ScopeAsync(ct));
        var list = new List<ResourceGroupInfo>();
        if (_settings.ResourceGroup is { } name)
            list.Add(ToInfo((await sub.GetResourceGroups().GetAsync(name, ct)).Value.Data));
        else
            await foreach (var rg in sub.GetResourceGroups().GetAllAsync(cancellationToken: ct)) list.Add(ToInfo(rg.Data));
        return list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static ResourceGroupInfo ToInfo(ResourceGroupData d) => new(d.Name, d.Location.ToString(), d.ResourceGroupProvisioningState);
    }

    public async Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var rg = ResourceGroup(scope);
        var sub = Subscription(scope);
        var list = new List<VmInfo>();

        // statusOnly=true returns instance-view power state; the resource-group list API has no such option.
        var vms = rg is not null ? rg.GetVirtualMachines().GetAllAsync(cancellationToken: ct) : sub.GetVirtualMachinesAsync(statusOnly: "true", cancellationToken: ct);
        await foreach (var vm in vms)
        {
            var d = vm.Data;
            list.Add(new("VM", d.Name, d.Id.ResourceGroupName ?? "", d.Location.ToString(), AzureMappers.Join(d.Zones),
                d.HardwareProfile?.VmSize?.ToString(), AzureMappers.PowerState(d.InstanceView?.Statuses?.Select(s => s.Code))));
        }

        var sets = rg is not null ? rg.GetVirtualMachineScaleSets().GetAllAsync(ct) : sub.GetVirtualMachineScaleSetsAsync(ct);
        await foreach (var set in sets)
        {
            var d = set.Data;
            list.Add(new("VMSS", d.Name, d.Id.ResourceGroupName ?? "", d.Location.ToString(), AzureMappers.Join(d.Zones),
                d.Sku?.Name, null, d.Sku?.Capacity is long c ? (int)c : null));
        }
        return list.OrderBy(v => v.ResourceGroup, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<StorageAccountInfo>> ListStorageAccountsAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var rg = ResourceGroup(scope);
        var accounts = rg is not null ? rg.GetStorageAccounts().GetAllAsync(ct) : Subscription(scope).GetStorageAccountsAsync(ct);
        var list = new List<StorageAccountInfo>();
        await foreach (var a in accounts)
        {
            var d = a.Data;
            list.Add(new(d.Name, d.Id.ResourceGroupName ?? "", d.Location.ToString(), d.Kind?.ToString(), d.Sku?.Name.ToString(),
                d.AccessTier?.ToString(), d.PublicNetworkAccess?.ToString()));
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<VnetInfo>> ListVnetsAsync(CancellationToken ct) =>
        (await VnetDataAsync(ct))
            .Select(d => new VnetInfo(d.Name, d.Id?.ResourceGroupName ?? "", d.Location?.ToString() ?? "",
                AzureMappers.Join(d.AddressSpace?.AddressPrefixes) ?? "—", d.Subnets.Count))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<SubnetInfo>> ListSubnetsAsync(CancellationToken ct) =>
        (await VnetDataAsync(ct))
            .SelectMany(v => v.Subnets.Select(s => new SubnetInfo(v.Name, s.Name, s.AddressPrefix ?? AzureMappers.Join(s.AddressPrefixes),
                s.NetworkSecurityGroup?.Id?.Name, s.RouteTable?.Id?.Name)))
            .OrderBy(s => s.Vnet, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<NsgRuleInfo>> ListNsgRulesAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var rg = ResourceGroup(scope);
        var nsgs = rg is not null ? rg.GetNetworkSecurityGroups().GetAllAsync(ct) : Subscription(scope).GetNetworkSecurityGroupsAsync(ct);
        var list = new List<NsgRuleInfo>();
        await foreach (var nsg in nsgs)
            foreach (var r in nsg.Data.SecurityRules)
                list.Add(new(nsg.Data.Name, r.Name, r.Direction?.ToString(), r.Priority, r.Access?.ToString(), r.Protocol?.ToString(),
                    AzureMappers.OneOrMany(r.DestinationPortRange, r.DestinationPortRanges),
                    AzureMappers.OneOrMany(r.SourceAddressPrefix, r.SourceAddressPrefixes),
                    AzureMappers.OneOrMany(r.DestinationAddressPrefix, r.DestinationAddressPrefixes)));
        return list.OrderBy(r => r.Nsg, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Direction).ThenBy(r => r.Priority).ToList();
    }

    public async Task<IReadOnlyList<ResourceTypeCount>> ResourceGraphSummaryAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        TenantResource? tenant = null;
        await foreach (var t in _arm.GetTenants().GetAllAsync(ct))
        {
            tenant = t;
            break;
        }
        if (tenant is null) throw new AzureError("No tenant is visible to this identity");

        var filter = _settings.ResourceGroup is { } rg ? $" | where resourceGroup =~ {AzureMappers.KqlString(rg)}" : "";
        var content = new ResourceQueryContent($"Resources{filter} | summarize count_=count() by type | order by count_ desc")
        {
            Options = new ResourceQueryRequestOptions { Top = 1000 },
        };
        // Pin to the chosen subscription when one was configured; otherwise query everything this identity can read.
        if (_settings.SubscriptionId is not null || _settings.ResourceGroup is not null) content.Subscriptions.Add(scope.SubscriptionId);

        var result = (await tenant.GetResourcesAsync(content, ct)).Value;
        return AzureMappers.ParseGraphRows(result.Data);
    }

    private async Task<List<Azure.ResourceManager.Network.VirtualNetworkData>> VnetDataAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var rg = ResourceGroup(scope);
        var vnets = rg is not null ? rg.GetVirtualNetworks().GetAllAsync(ct) : Subscription(scope).GetVirtualNetworksAsync(ct);
        var list = new List<Azure.ResourceManager.Network.VirtualNetworkData>();
        await foreach (var v in vnets) list.Add(v.Data);
        return list;
    }

    private SubscriptionResource Subscription(ResolvedScope scope) =>
        _arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(scope.SubscriptionId));

    private ResourceGroupResource? ResourceGroup(ResolvedScope scope) =>
        _settings.ResourceGroup is { } name
            ? _arm.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(scope.SubscriptionId, name))
            : null;

    private async Task<ResolvedScope> ScopeAsync(CancellationToken ct)
    {
        if (_scope is { } cached) return cached;
        await _scopeGate.WaitAsync(ct);
        try
        {
            if (_scope is { } again) return again;
            var visible = new List<string>();
            if (_settings.SubscriptionId is null)
                await foreach (var s in _arm.GetSubscriptions().GetAllAsync(ct)) visible.Add(s.Data.SubscriptionId);
            return _scope = ScopeResolver.Choose(_settings.SubscriptionId, visible, _settings.ResourceGroup);
        }
        finally
        {
            _scopeGate.Release();
        }
    }
}
```

> **Compile note:** the code is written against Azure.ResourceManager.* 1.14/1.17/1.7/1.1.1. If a nullable annotation differs slightly (for example `d.Location` being `AzureLocation` rather than `AzureLocation?` on network models), adjust only the `?.`/`??` operators in that expression; don't change behaviour. Names are fully qualified where `Azure.ResourceManager.Network` and `Azure.ResourceManager.Storage` would otherwise be ambiguous.

- [ ] **Step 5: Implement `AzureSdkLogging` and switch DI to the live provider**

`src/AzureDash/AzureSdkLogging.cs`:
```csharp
using System.Diagnostics.Tracing;
using Azure.Core.Diagnostics;

namespace AzureDash;

/// <summary>
/// AZURE_DEBUG=true: forwards Azure SDK EventSource output (HTTP requests, retries, token acquisition) to logging.
/// Azure.Core redacts Authorization and other non-allow-listed headers; request/response bodies are not logged.
/// </summary>
public sealed class AzureSdkLogging(ILoggerFactory loggers) : IDisposable
{
    private AzureEventSourceListener? _listener;

    public void Start()
    {
        var log = loggers.CreateLogger("Azure.Sdk");
        _listener ??= new AzureEventSourceListener(
            (e, message) => log.LogInformation("{Source}/{Event}: {Message}", e.EventSource.Name, e.EventName, message),
            EventLevel.Verbose);
    }

    public void Dispose() => _listener?.Dispose();
}
```

In `src/AzureDash/Program.cs`:
- replace the stub registration
  ```csharp
  builder.Services.AddSingleton<Func<IAzureProvider>>(_ => () => throw new AzureError("no Azure provider is registered"));
  ```
  with
  ```csharp
  builder.Services.AddSingleton<Func<IAzureProvider>>(sp => () =>
      new LiveAzureProvider(sp.GetRequiredService<CredentialProvider>(), sp.GetRequiredService<AppSettings>()));
  builder.Services.AddSingleton<AzureSdkLogging>();
  ```
- after `builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);`, add:
  ```csharp
  if (settings.AzureDebug)
      builder.Logging.AddFilter("AzureDash", LogLevel.Debug).AddFilter("Azure.Sdk", LogLevel.Information);
  ```
- right after `var app = builder.Build();`, add:
  ```csharp
  if (settings.AzureDebug) app.Services.GetRequiredService<AzureSdkLogging>().Start();
  ```
- add `using AzureDash;` to the top.

- [ ] **Step 6: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS. Test factories still inject `FakeAzureProvider`, so nothing calls Azure.

- [ ] **Step 7: Optional smoke test against real Azure (needs `az login`)**

```bash
az account show --query '{sub:id, tenant:tenantId}' -o tsv
AUTH_MODE=dev ASPNETCORE_HTTP_PORTS=8080 dotnet run --project src/AzureDash &
curl --retry 20 --retry-connrefused --retry-delay 1 -fsS http://localhost:8080/api/azure/subscription
curl -fsS http://localhost:8080/api/azure/resourcegraph | head -c 400; echo
kill %1
```
Expected: JSON with `"error":null` and your subscription, or an explicit RBAC or credential error in `error`. A 500 is always wrong.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add live Azure Resource Manager and Resource Graph provider"
```

---

### Task 9: Identity and IMDS panels

**Files:**
- Create: `src/AzureDash/Identity/IdentityInfoService.cs`, `src/AzureDash/Imds/ImdsClient.cs`, `src/AzureDash/Endpoints/IdentityEndpoints.cs`
- Create: `src/AzureDash/Components/Partials/{IdentityPartial,ImdsPartial,ClaimsTable}.razor`, `src/AzureDash/Pages/Identity.cshtml`
- Modify: `src/AzureDash/Program.cs`, `tests/AzureDash.Tests/Support/AppFactory.cs`
- Create: `tests/AzureDash.Tests/Support/{FakeTokenCredential,StubHandler}.cs`
- Test: `tests/AzureDash.Tests/IdentityInfoServiceTests.cs`, `tests/AzureDash.Tests/ImdsClientTests.cs`, `tests/AzureDash.Tests/IdentityPagesTests.cs`

**Interfaces:**
- Consumes: `CredentialProvider`, `CredentialSelection`, `SpiffeAssertionSource`, `JwtDisplay`, `JwtSummary`, `CloudEndpoints` (Task 7); `AzureError` (Task 5); `Fmt` (Task 4); `AppSettings`.
- Produces:
  - `record IdentityInfo(string Mode, string Reason, string? CredentialType, string? ClientId, string? TenantId, string? AuthorityHost, string? TokenFile, JwtSummary? FederatedToken, JwtSummary? AccessToken, string? Error, DateTimeOffset FetchedAt)`
  - `IdentityInfoService(CredentialProvider, AppSettings, TimeProvider).GetAsync(CancellationToken)`. It never throws, except when the request is cancelled.
  - `record ImdsInfo(bool Available, string? Reason, IReadOnlyDictionary<string,string>? Compute, DateTimeOffset FetchedAt)`
  - `ImdsClient(HttpClient, AppSettings, TimeProvider)`:
    - `GetAsync(CancellationToken)` caches its result for `CacheTtl`
    - `ImdsClient.Fields`, `ImdsClient.Timeout` (1 s), `ImdsClient.HttpClientName = "imds"`
  - Routes: `GET /identity`, `/partials/identity`, `/partials/imds`, `/api/identity`, `/api/imds`
  - Test helper: `AppFactory.Credentials`, a settable `CredentialProvider`. By default it's a provider whose `Get()` throws `"no credentials configured in tests"`.

- [ ] **Step 1: Write the test helpers and update `AppFactory`**

`tests/AzureDash.Tests/Support/FakeTokenCredential.cs`:
```csharp
using Azure.Core;

namespace AzureDash.Tests.Support;

public sealed class FakeTokenCredential(Func<string> token) : TokenCredential
{
    public List<string[]> Requests { get; } = [];

    public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
    {
        Requests.Add(context.Scopes);
        return new AccessToken(token(), DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
        ValueTask.FromResult(GetToken(context, ct));
}
```

`tests/AzureDash.Tests/Support/StubHandler.cs`:
```csharp
using System.Net;

namespace AzureDash.Tests.Support;

public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return respond(request, ct);
    }

    public static StubHandler Returns(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
}
```

In `tests/AzureDash.Tests/Support/AppFactory.cs`:
- add `using AzureDash.Identity;`
- add this property:
  ```csharp
  public CredentialProvider Credentials { get; init; } = new(
      () => throw new InvalidOperationException("no credentials configured in tests"),
      () => (AuthMode.Dev, "test default"));
  ```
- add these lines at the end of the `ConfigureTestServices` lambda:
  ```csharp
  services.RemoveAll<CredentialProvider>();
  services.AddSingleton(Credentials);
  ```

- [ ] **Step 2: Write the failing tests**

`tests/AzureDash.Tests/IdentityInfoServiceTests.cs`:
```csharp
using Azure.Identity;
using AzureDash.Configuration;
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class IdentityInfoServiceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("identity-").FullName;
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public void Dispose() => Directory.Delete(_dir, true);

    string TokenFile(string content)
    {
        var path = Path.Combine(_dir, "token");
        File.WriteAllText(path, content);
        return path;
    }

    static readonly string AccessJwt = TestJwt.Make(new
    {
        aud = "https://management.azure.com", iss = "https://sts.windows.net/tid/", oid = "object-id-1", tid = "tid",
        appid = "cid", idtyp = "app", ver = "1.0", xms_mirid = "/subscriptions/s/resourcegroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-azure-dash",
        exp = 1767232800,
    });

    IdentityInfoService Service(AuthMode mode, string? tokenFile, FakeTokenCredential credential, AzureCloud cloud = AzureCloud.Public) =>
        new(new CredentialProvider(
                () => new CredentialSelection(mode, "test reason", credential, "WorkloadIdentityCredential", "cid", "tid",
                    new Uri("https://login.microsoftonline.com/"), tokenFile),
                () => (mode, "test reason")),
            new AppSettings { Cloud = cloud }, _time);

    [Fact]
    public async Task Shows_federated_and_access_token_claims()
    {
        var federated = TestJwt.Make(new { iss = "https://oidc.example/", sub = "system:serviceaccount:azure-dash:azure-dash", aud = "api://AzureADTokenExchange", exp = 1767229200 });
        var credential = new FakeTokenCredential(() => AccessJwt);
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile(federated), credential).GetAsync(CancellationToken.None);

        Assert.Null(info.Error);
        Assert.Equal("workload-identity", info.Mode);
        Assert.Equal("cid", info.ClientId);
        Assert.Equal("system:serviceaccount:azure-dash:azure-dash", info.FederatedToken!.Claims["sub"]);
        Assert.Equal("object-id-1", info.AccessToken!.Claims["oid"]);
        Assert.EndsWith("id-azure-dash", info.AccessToken.Claims["xms_mirid"]);
        Assert.Equal(new[] { "https://management.azure.com/.default" }, credential.Requests.Single());
        Assert.Equal(_time.Now, info.FetchedAt);
    }

    [Fact]
    public async Task Requests_the_sovereign_cloud_scope()
    {
        var credential = new FakeTokenCredential(() => AccessJwt);
        await Service(AuthMode.ClientSecret, null, credential, AzureCloud.China).GetAsync(CancellationToken.None);
        Assert.Equal(new[] { "https://management.chinacloudapi.cn/.default" }, credential.Requests.Single());
    }

    [Fact]
    public async Task Spiffe_mode_reads_through_the_assertion_source()
    {
        var jwt = TestJwt.Make(new { sub = "spiffe://td/ns/azure-dash-ztwim/sa/azure-dash" });
        var info = await Service(AuthMode.Spiffe, TokenFile(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jwt))),
            new FakeTokenCredential(() => AccessJwt)).GetAsync(CancellationToken.None);
        Assert.Equal("spiffe://td/ns/azure-dash-ztwim/sa/azure-dash", info.FederatedToken!.Claims["sub"]);
    }

    [Fact]
    public async Task Garbage_token_file_is_a_decode_error_and_access_token_still_attempted()
    {
        var credential = new FakeTokenCredential(() => AccessJwt);
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile("definitely not a jwt"), credential).GetAsync(CancellationToken.None);
        Assert.Contains("could not decode token", info.FederatedToken!.Error);
        Assert.Equal("object-id-1", info.AccessToken!.Claims["oid"]);
    }

    [Fact]
    public async Task Missing_token_file_is_reported_not_thrown()
    {
        var info = await Service(AuthMode.WorkloadIdentity, Path.Combine(_dir, "absent"), new FakeTokenCredential(() => AccessJwt))
            .GetAsync(CancellationToken.None);
        Assert.StartsWith("could not read", info.FederatedToken!.Error);
    }

    [Fact]
    public async Task Token_exchange_failure_is_translated()
    {
        var credential = new FakeTokenCredential(() => throw new AuthenticationFailedException(
            "WorkloadIdentityCredential authentication failed\nAADSTS700213: No matching federated identity record found for presented assertion subject."));
        var info = await Service(AuthMode.WorkloadIdentity, TokenFile(TestJwt.Make(new { sub = "s" })), credential).GetAsync(CancellationToken.None);
        Assert.Contains("AADSTS700213", info.Error);
        Assert.Null(info.AccessToken);
        Assert.Equal("s", info.FederatedToken!.Claims["sub"]);
    }

    [Fact]
    public async Task Credential_construction_failure_is_reported()
    {
        var service = new IdentityInfoService(
            new CredentialProvider(() => throw new InvalidOperationException("AUTH_MODE spiffe requires AZURE_CLIENT_ID"), () => (AuthMode.Spiffe, "AUTH_MODE=spiffe")),
            new AppSettings(), _time);
        var info = await service.GetAsync(CancellationToken.None);
        Assert.Equal("spiffe", info.Mode);
        Assert.Equal("InvalidOperationException: AUTH_MODE spiffe requires AZURE_CLIENT_ID", info.Error);
        Assert.Null(info.CredentialType);
    }
}
```

`tests/AzureDash.Tests/ImdsClientTests.cs`:
```csharp
using System.Diagnostics;
using System.Net;
using AzureDash.Configuration;
using AzureDash.Imds;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class ImdsClientTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    const string Compute = """
        {"name":"aks-nodepool1-12345678-vmss_0","location":"eastus","zone":"1","vmSize":"Standard_D4s_v5",
         "subscriptionId":"sub-1","resourceGroupName":"MC_rg-demo_aks-demo_eastus","vmScaleSetName":"aks-nodepool1-12345678-vmss",
         "osType":"Linux","vmId":"vm-id-1","tagsList":[{"name":"x","value":"y"}],"publicKeys":[]}
        """;

    ImdsClient Client(StubHandler handler, AppSettings? settings = null) =>
        new(new HttpClient(handler), settings ?? new AppSettings(), _time);

    [Fact]
    public async Task Returns_compute_fields_and_sends_metadata_header()
    {
        var handler = StubHandler.Returns(Compute);
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.True(info.Available);
        Assert.Equal("MC_rg-demo_aks-demo_eastus", info.Compute!["resourceGroupName"]);
        Assert.Equal("aks-nodepool1-12345678-vmss", info.Compute["vmScaleSetName"]);
        Assert.False(info.Compute.ContainsKey("tagsList"));
        var request = handler.Requests.Single();
        Assert.Equal("true", request.Headers.GetValues("Metadata").Single());
        Assert.Equal("http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01&format=json", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Hanging_endpoint_times_out_quickly()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var sw = Stopwatch.StartNew();
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("did not answer within 1s", info.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Http_error_is_unavailable()
    {
        var info = await Client(StubHandler.Returns("{}", HttpStatusCode.BadRequest)).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.StartsWith("IMDS returned 400", info.Reason);
    }

    [Theory]
    [InlineData("<html>proxy</html>")]
    [InlineData("[1,2]")]
    public async Task Non_json_or_non_object_is_unavailable(string body)
    {
        var info = await Client(StubHandler.Returns(body)).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("not a JSON object", info.Reason);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var info = await Client(handler).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Contains("Connection refused", info.Reason);
    }

    [Fact]
    public async Task Disabled_makes_no_request()
    {
        var handler = StubHandler.Returns(Compute);
        var info = await Client(handler, new AppSettings { ImdsEnabled = false }).GetAsync(CancellationToken.None);
        Assert.False(info.Available);
        Assert.Equal("disabled (IMDS_ENABLED=false)", info.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caches_for_ttl()
    {
        var handler = StubHandler.Returns(Compute);
        var client = Client(handler, new AppSettings { CacheTtl = TimeSpan.FromSeconds(60) });
        await client.GetAsync(CancellationToken.None);
        await client.GetAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        _time.Advance(TimeSpan.FromSeconds(61));
        await client.GetAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }
}
```

`tests/AzureDash.Tests/IdentityPagesTests.cs`:
```csharp
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
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~IdentityInfoServiceTests|FullyQualifiedName~ImdsClientTests|FullyQualifiedName~IdentityPagesTests"`
Expected: a build error, because `IdentityInfoService` and `ImdsClient` don't exist yet.

- [ ] **Step 4: Implement `IdentityInfoService`**

`src/AzureDash/Identity/IdentityInfoService.cs`:
```csharp
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
```

- [ ] **Step 5: Implement `ImdsClient`**

`src/AzureDash/Imds/ImdsClient.cs`:
```csharp
using System.Text.Json;
using AzureDash.Configuration;

namespace AzureDash.Imds;

public sealed record ImdsInfo(bool Available, string? Reason, IReadOnlyDictionary<string, string>? Compute, DateTimeOffset FetchedAt);

/// <summary>
/// Azure Instance Metadata Service. On AKS this describes the NODE (VMSS instance), not the pod. Unreachable IMDS
/// (not on Azure, or AKS --enable-imds-restriction) is a normal, displayable state.
/// </summary>
public sealed class ImdsClient(HttpClient http, AppSettings settings, TimeProvider time)
{
    public const string HttpClientName = "imds";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    public static readonly IReadOnlyList<string> Fields =
        ["name", "location", "zone", "vmSize", "subscriptionId", "resourceGroupName", "vmScaleSetName", "osType", "vmId"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImdsInfo? _cached;

    public async Task<ImdsInfo> GetAsync(CancellationToken ct)
    {
        if (!settings.ImdsEnabled) return new(false, "disabled (IMDS_ENABLED=false)", null, time.GetUtcNow());
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } c && time.GetUtcNow() - c.FetchedAt < settings.CacheTtl) return c;
            return _cached = await FetchAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImdsInfo> FetchAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var url = $"http://169.254.169.254/metadata/instance/compute?api-version={Uri.EscapeDataString(settings.ImdsApiVersion)}&format=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Metadata", "true");
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode) return Unavailable($"IMDS returned {(int)response.StatusCode} {response.ReasonPhrase}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Unavailable("IMDS response is not a JSON object");
            var compute = new Dictionary<string, string>();
            foreach (var field in Fields)
                if (doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    compute[field] = s;
            return new(true, null, compute, time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("IMDS did not answer within 1s: not running on an Azure VM, or pod access is blocked (AKS --enable-imds-restriction)");
        }
        catch (HttpRequestException ex)
        {
            return Unavailable($"IMDS not reachable: {ex.Message}");
        }
        catch (JsonException)
        {
            return Unavailable("IMDS response is not a JSON object");
        }
    }

    private ImdsInfo Unavailable(string reason) => new(false, reason, null, time.GetUtcNow());
}
```

- [ ] **Step 6: Write the components, the page and the endpoints**

`src/AzureDash/Components/Partials/ClaimsTable.razor`:
```razor
@using AzureDash.Identity
@if (Summary.Error is not null)
{
    <div class="error">@Summary.Error</div>
}
else
{
    <dl class="kv">
        @foreach (var (name, value) in Summary.Claims)
        {
            <dt>@name</dt><dd>@value</dd>
        }
        @if (Summary.ExpiresAt is { } exp)
        {
            <dt>expires</dt><dd>@Fmt.Time(exp) (@Fmt.Until(exp, Now))</dd>
        }
    </dl>
}

@code {
    [Parameter, EditorRequired] public JwtSummary Summary { get; set; } = default!;
    [Parameter] public DateTimeOffset Now { get; set; }
}
```

`src/AzureDash/Components/Partials/IdentityPartial.razor`:
```razor
@using AzureDash.Identity
<h2>Workload identity</h2>
@if (Info.Error is not null)
{
    <div class="error">@Info.Error</div>
}
<dl class="kv">
    <dt>Auth mode</dt><dd>@Info.Mode</dd>
    <dt>Why</dt><dd>@Info.Reason</dd>
    <dt>Credential</dt><dd>@Fmt.Or(Info.CredentialType)</dd>
    <dt>Client ID</dt><dd>@Fmt.Or(Info.ClientId)</dd>
    <dt>Tenant ID</dt><dd>@Fmt.Or(Info.TenantId)</dd>
    <dt>Authority host</dt><dd>@Fmt.Or(Info.AuthorityHost)</dd>
    <dt>Token file</dt><dd>@Fmt.Or(Info.TokenFile)</dd>
</dl>
@if (Info.FederatedToken is { } federated)
{
    <h3>Federated token (presented to Entra)</h3>
    <p class="muted">Issued by the cluster: the Kubernetes service-account issuer, or SPIRE for ZTWIM. Entra matches its iss, sub and aud against a federated identity credential.</p>
    <ClaimsTable Summary="federated" Now="Info.FetchedAt" />
}
@if (Info.AccessToken is { } access)
{
    <h3>Entra access token (Azure Resource Manager)</h3>
    <p class="muted">Returned by Entra for the federated token. oid is the identity's object ID and appid/azp its client ID; xms_mirid names the managed identity when there is one.</p>
    <ClaimsTable Summary="access" Now="Info.FetchedAt" />
}
<p class="muted">Checked @Fmt.Time(Info.FetchedAt). Raw tokens are never displayed. · <a href="#" hx-get="/partials/identity" hx-target="closest section">refresh</a></p>

@code {
    [Parameter, EditorRequired] public IdentityInfo Info { get; set; } = default!;
}
```

`src/AzureDash/Components/Partials/ImdsPartial.razor`:
```razor
@using AzureDash.Imds
<h2>Instance metadata (IMDS)</h2>
@if (!Info.Available)
{
    <p class="muted">Unavailable: @Info.Reason</p>
}
else
{
    <p class="muted">This describes the <strong>node</strong> (the VM scale set instance) this pod runs on, not the pod. On AKS the resource group is the node resource group (MC_…).</p>
    <dl class="kv">
        @foreach (var (key, value) in Info.Compute!)
        {
            <dt>@key</dt><dd>@value</dd>
        }
    </dl>
}
<p class="muted">Checked @Fmt.Time(Info.FetchedAt)</p>

@code {
    [Parameter, EditorRequired] public ImdsInfo Info { get; set; } = default!;
}
```

`src/AzureDash/Pages/Identity.cshtml`:
```cshtml
@page "/identity"
@{
    ViewData["Title"] = "Identity";
    ViewData["Nav"] = "identity";
}
<div class="grid">
    <section class="card" hx-get="/partials/identity" hx-trigger="load">
        <h2>Workload identity</h2>
        <p class="muted">Loading…</p>
    </section>
    <section class="card" hx-get="/partials/imds" hx-trigger="load">
        <h2>Instance metadata (IMDS)</h2>
        <p class="muted">Loading…</p>
    </section>
</div>
```

`src/AzureDash/Endpoints/IdentityEndpoints.cs`:
```csharp
using AzureDash.Components.Partials;
using AzureDash.Identity;
using AzureDash.Imds;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/identity", async (IdentityInfoService s, CancellationToken ct) => Results.Json(await s.GetAsync(ct)));
        app.MapGet("/partials/identity", async (IdentityInfoService s, CancellationToken ct) =>
            new RazorComponentResult<IdentityPartial>(new { Info = await s.GetAsync(ct) }));
        app.MapGet("/api/imds", async (ImdsClient c, CancellationToken ct) => Results.Json(await c.GetAsync(ct)));
        app.MapGet("/partials/imds", async (ImdsClient c, CancellationToken ct) =>
            new RazorComponentResult<ImdsPartial>(new { Info = await c.GetAsync(ct) }));
        return app;
    }
}
```

In `src/AzureDash/Program.cs`:
- add `using AzureDash.Imds;`
- after the `AzureService` registration, add:
  ```csharp
  builder.Services.AddSingleton<IdentityInfoService>();
  builder.Services.AddHttpClient(ImdsClient.HttpClientName)
      .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, ConnectTimeout = ImdsClient.Timeout });
  builder.Services.AddSingleton(sp => new ImdsClient(
      sp.GetRequiredService<IHttpClientFactory>().CreateClient(ImdsClient.HttpClientName),
      sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<TimeProvider>()));
  ```
- after `app.MapAzureEndpoints();`, add:
  ```csharp
  app.MapIdentityEndpoints();
  ```

- [ ] **Step 7: Run all tests and confirm they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add identity (federated + Entra token claims) and IMDS panels"
```

---

### Task 10: Container image, `--healthcheck` self-probe, podman helper

**Files:**
- Create: `src/AzureDash/SelfProbe.cs`, `Containerfile`, `.containerignore`, `scripts/run-podman.sh`
- Modify: `src/AzureDash/Program.cs`
- Test: `tests/AzureDash.Tests/SelfProbeTests.cs`

**Interfaces:**
- Consumes: `EnvLookup`, `AppSettings.Blank` (Task 1), `StubHandler` (Task 9).
- Produces:
  - `SelfProbe.LocalUrl(EnvLookup) → Uri`: `http://127.0.0.1:<first ASPNETCORE_HTTP_PORTS port, default 8080>/healthz/live`
  - `SelfProbe.RunAsync(Uri, HttpMessageHandler, TextWriter) → Task<int>`: returns 0 when healthy, 1 otherwise
  - `dotnet AzureDash.dll --healthcheck`

- [ ] **Step 1: Write the failing tests**

`tests/AzureDash.Tests/SelfProbeTests.cs`:
```csharp
using System.Net;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class SelfProbeTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/healthz/live")]
    [InlineData("9090", "http://127.0.0.1:9090/healthz/live")]
    [InlineData("9090;8081", "http://127.0.0.1:9090/healthz/live")]
    public void Local_url_uses_first_http_port(string? ports, string expected) =>
        Assert.Equal(new Uri(expected), SelfProbe.LocalUrl(name => name == "ASPNETCORE_HTTP_PORTS" ? ports : null));

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Exit_code_follows_status(HttpStatusCode status, int expected)
    {
        var output = new StringWriter();
        var code = await SelfProbe.RunAsync(new Uri("http://127.0.0.1:8080/healthz/live"), StubHandler.Returns("{}", status), output);
        Assert.Equal(expected, code);
        Assert.Contains(((int)status).ToString(), output.ToString());
    }

    [Fact]
    public async Task Connection_failure_is_unhealthy()
    {
        var output = new StringWriter();
        var code = await SelfProbe.RunAsync(new Uri("http://127.0.0.1:8080/healthz/live"),
            new StubHandler((_, _) => throw new HttpRequestException("refused")), output);
        Assert.Equal(1, code);
        Assert.Contains("unhealthy: refused", output.ToString());
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~SelfProbeTests"`
Expected: a build error, because `SelfProbe` doesn't exist yet.

- [ ] **Step 3: Implement `SelfProbe` and hook it into `Program`**

`src/AzureDash/SelfProbe.cs`:
```csharp
using AzureDash.Configuration;

namespace AzureDash;

/// <summary>`dotnet AzureDash.dll --healthcheck`: container HEALTHCHECK without curl in the image.</summary>
public static class SelfProbe
{
    public static Uri LocalUrl(EnvLookup env)
    {
        var ports = AppSettings.Blank(env("ASPNETCORE_HTTP_PORTS")) ?? "8080";
        var port = ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return new Uri($"http://127.0.0.1:{port}/healthz/live");
    }

    public static async Task<int> RunAsync(Uri url, HttpMessageHandler handler, TextWriter output)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(url);
            await output.WriteLineAsync($"{(int)response.StatusCode} {url}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync($"unhealthy: {ex.Message}");
            return 1;
        }
    }
}
```

In `src/AzureDash/Program.cs`, insert these lines just below the `using` directives, before `EnvLookup env = ...`:
```csharp
if (args.Contains("--healthcheck"))
    return await SelfProbe.RunAsync(SelfProbe.LocalUrl(Environment.GetEnvironmentVariable), new SocketsHttpHandler(), Console.Out);
```

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 4: Write the Containerfile, the ignore file and the podman helper**

`Containerfile`:
```dockerfile
# syntax=docker/dockerfile:1
# Build stage runs on the build host's architecture and cross-compiles for TARGETARCH (no QEMU needed).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AzureDash/AzureDash.csproj src/AzureDash/
RUN dotnet restore src/AzureDash/AzureDash.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/AzureDash/AzureDash.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="azure-dash" \
      org.opencontainers.image.description="Azure Workload Identity demo dashboard for AKS and OpenShift" \
      org.opencontainers.image.source="https://github.com/kenmoini/azure-dash" \
      io.openshift.expose-services="8080:http"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    AUTH_MODE=auto \
    AZURE_CACHE_TTL_SECONDS=60 \
    CONTROLS_ENABLED=true \
    LOG_LEVEL=Information \
    AZURE_DEBUG=false
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 CMD ["dotnet", "/app/AzureDash.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/AzureDash.dll"]
```

`.containerignore`:
```
.git
.github
**/bin
**/obj
tests
docs
deploy
scripts
*.md
.claude
.superpowers
```

`scripts/run-podman.sh`:
```bash
#!/usr/bin/env bash
# Run azure-dash locally in podman using a service principal (client-secret mode):
#   AZURE_TENANT_ID=... AZURE_CLIENT_ID=... AZURE_CLIENT_SECRET=... [AZURE_SUBSCRIPTION_ID=...] scripts/run-podman.sh [image]
# Dev mode (az login) does not work inside the container because the image has no Azure CLI.
set -euo pipefail
IMAGE="${1:-localhost/azure-dash:dev}"
args=()
for v in AZURE_TENANT_ID AZURE_CLIENT_ID AZURE_CLIENT_SECRET AZURE_SUBSCRIPTION_ID AZURE_RESOURCE_GROUP AZURE_CLOUD AUTH_MODE LOG_LEVEL AZURE_DEBUG CONTROLS_ENABLED; do
  if [[ -n "${!v:-}" ]]; then args+=(-e "$v"); fi
done
exec podman run --rm -it --name azure-dash --cpus 1 --memory 512m -p 8080:8080 ${args[@]+"${args[@]}"} "$IMAGE"
```
Run: `chmod +x scripts/run-podman.sh`

- [ ] **Step 5: Build the image and check that it's healthy and non-root**

```bash
podman build --format docker -t localhost/azure-dash:dev .
podman run -d --rm --name azure-dash-test -p 18080:8080 localhost/azure-dash:dev
curl --retry 20 --retry-connrefused --retry-delay 1 -fsS http://localhost:18080/healthz/live; echo
podman exec azure-dash-test id -u
podman exec azure-dash-test dotnet /app/AzureDash.dll --healthcheck; echo "exit=$?"
curl -fsS http://localhost:18080/api/identity | head -c 300; echo
curl -fsS http://localhost:18080/api/imds; echo
podman rm -f azure-dash-test
```
Expected:
- `{"status":"ok"}`
- `1654`
- `200 http://127.0.0.1:8080/healthz/live` followed by `exit=0`
- Identity JSON with `"mode":"dev"` and an `error` mentioning the Azure CLI. This is expected because the image has no CLI.
- IMDS JSON with `"available":false`, meaning it isn't running on Azure.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add container image with self-probe healthcheck and podman helper"
```

---

### Task 11: Kustomize base and AKS Workload ID overlay

**Files:**
- Create: `deploy/kubernetes/{kustomization,namespace,serviceaccount,configmap,deployment,service}.yaml`
- Create: `deploy/aks/{kustomization,ingress}.yaml`, `deploy/aks/README.md`

**Interfaces:**
- Consumes: the container contract from Task 10 (port 8080, probes, env vars) and the env names from the Global Constraints.
- Produces:
  - Base resources: Namespace `azure-dash`, ServiceAccount `azure-dash`, ConfigMap `azure-dash-config`, Deployment `azure-dash` (container index 0 named `azure-dash`), and Service `azure-dash` (port 8080, name `http`).
  - The base Deployment has empty `initContainers`, `volumes` and `volumeMounts` lists, and non-empty `env` and `envFrom` lists, so overlays can append with JSON6902 `add .../-`.
  - Overlays target resources with `target: {kind, name}` patches.

- [ ] **Step 1: Write the base manifests**

`deploy/kubernetes/kustomization.yaml`:
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: azure-dash
resources:
  - namespace.yaml
  - serviceaccount.yaml
  - configmap.yaml
  - deployment.yaml
  - service.yaml
```

`deploy/kubernetes/namespace.yaml`:
```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: azure-dash
```

`deploy/kubernetes/serviceaccount.yaml`:
```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: azure-dash
```

`deploy/kubernetes/configmap.yaml`:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: azure-dash-config
data:
  AUTH_MODE: auto
  AZURE_SUBSCRIPTION_ID: ""
  AZURE_RESOURCE_GROUP: ""
  AZURE_CLOUD: public
  AZURE_CACHE_TTL_SECONDS: "60"
  CONTROLS_ENABLED: "true"
  LOG_LEVEL: Information
  AZURE_DEBUG: "false"
```

`deploy/kubernetes/deployment.yaml`:
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: azure-dash
  labels:
    app: azure-dash
spec:
  replicas: 1
  selector:
    matchLabels:
      app: azure-dash
  template:
    metadata:
      labels:
        app: azure-dash
    spec:
      serviceAccountName: azure-dash
      securityContext:
        runAsNonRoot: true
        seccompProfile:
          type: RuntimeDefault
      initContainers: []
      containers:
        - name: azure-dash
          image: ghcr.io/kenmoini/azure-dash:latest
          imagePullPolicy: Always
          ports:
            - name: http
              containerPort: 8080
          envFrom:
            - configMapRef:
                name: azure-dash-config
          env:
            - name: POD_NAME
              valueFrom: {fieldRef: {fieldPath: metadata.name}}
            - name: POD_NAMESPACE
              valueFrom: {fieldRef: {fieldPath: metadata.namespace}}
            - name: NODE_NAME
              valueFrom: {fieldRef: {fieldPath: spec.nodeName}}
            - name: POD_IP
              valueFrom: {fieldRef: {fieldPath: status.podIP}}
            - name: SERVICE_ACCOUNT
              valueFrom: {fieldRef: {fieldPath: spec.serviceAccountName}}
          resources:
            requests: {cpu: 100m, memory: 128Mi}
            limits: {cpu: "1", memory: 512Mi}
          securityContext:
            allowPrivilegeEscalation: false
            capabilities: {drop: [ALL]}
          livenessProbe:
            httpGet: {path: /healthz/live, port: http}
            initialDelaySeconds: 10
            periodSeconds: 5
            failureThreshold: 3
          readinessProbe:
            httpGet: {path: /healthz/ready, port: http}
            periodSeconds: 5
            failureThreshold: 2
          volumeMounts: []
      volumes: []
```

`deploy/kubernetes/service.yaml`:
```yaml
apiVersion: v1
kind: Service
metadata:
  name: azure-dash
  labels:
    app: azure-dash
spec:
  selector:
    app: azure-dash
  ports:
    - name: http
      port: 8080
      targetPort: http
```

Run: `kubectl kustomize deploy/kubernetes | grep -E '^kind:|namespace: azure-dash' | sort | uniq -c`
Expected: one each of `kind: ConfigMap`, `Deployment`, `Namespace`, `Service` and `ServiceAccount`, plus 4 lines of `namespace: azure-dash`, one for each namespaced resource.

- [ ] **Step 2: Write the AKS overlay**

`deploy/aks/kustomization.yaml`:
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: azure-dash
resources:
  - ../kubernetes
  - ingress.yaml
patches:
  # Replace with the user-assigned managed identity's client ID (or annotate after apply; see README).
  - target: {kind: ServiceAccount, name: azure-dash}
    patch: |-
      - op: add
        path: /metadata/annotations
        value:
          azure.workload.identity/client-id: "00000000-0000-0000-0000-000000000000"
  # The Workload ID webhook only mutates pods carrying this label.
  - target: {kind: Deployment, name: azure-dash}
    patch: |-
      - op: add
        path: /spec/template/metadata/labels/azure.workload.identity~1use
        value: "true"
```

`deploy/aks/ingress.yaml`:
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: azure-dash
spec:
  ingressClassName: webapprouting.kubernetes.azure.com
  rules:
    - http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: azure-dash
                port:
                  name: http
```

Run: `kubectl kustomize deploy/aks | grep -E 'azure.workload.identity/(use|client-id)|ingressClassName'`
Expected: three lines, in this order: the `azure.workload.identity/client-id` annotation on the ServiceAccount, the `azure.workload.identity/use: "true"` label on the pod template, and `ingressClassName: webapprouting.kubernetes.azure.com`.

- [ ] **Step 3: Write `deploy/aks/README.md`**

````markdown
# azure-dash on AKS with Microsoft Entra Workload ID

The pod's projected service-account token (issued by the cluster's OIDC issuer) is exchanged with Entra for an
access token of a **user-assigned managed identity** (UAMI), using a **federated identity credential** (FIC).
The Workload ID webhook injects `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE` and
`AZURE_AUTHORITY_HOST` into pods labeled `azure.workload.identity/use: "true"`; azure-dash's `AUTH_MODE=auto`
then selects `WorkloadIdentityCredential`.

## 1. Enable the OIDC issuer and Workload ID

```bash
export RESOURCE_GROUP=rg-azure-dash CLUSTER=aks-azure-dash IDENTITY=id-azure-dash
export SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az aks update -g "$RESOURCE_GROUP" -n "$CLUSTER" --enable-oidc-issuer --enable-workload-identity
# Optional, for the Ingress in this overlay:
az aks approuting enable -g "$RESOURCE_GROUP" -n "$CLUSTER"
export ISSUER=$(az aks show -g "$RESOURCE_GROUP" -n "$CLUSTER" --query oidcIssuerProfile.issuerUrl -o tsv)
```

## 2. Create the identity, federate it with the service account, and grant Reader

```bash
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
export PRINCIPAL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)

az identity federated-credential create --name azure-dash \
  --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$ISSUER" \
  --subject system:serviceaccount:azure-dash:azure-dash \
  --audience api://AzureADTokenExchange

az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
  --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

For least privilege, scope Reader to a single resource group instead
(`--scope /subscriptions/$SUBSCRIPTION_ID/resourceGroups/<rg>`) and set both `AZURE_SUBSCRIPTION_ID` and
`AZURE_RESOURCE_GROUP` in `deploy/kubernetes/configmap.yaml` (or with `kubectl set env`).

## 3. Deploy

```bash
az aks get-credentials -g "$RESOURCE_GROUP" -n "$CLUSTER"
kubectl apply -k deploy/aks
kubectl -n azure-dash annotate sa azure-dash azure.workload.identity/client-id="$CLIENT_ID" --overwrite
kubectl -n azure-dash rollout restart deploy/azure-dash
kubectl -n azure-dash rollout status deploy/azure-dash
kubectl -n azure-dash exec deploy/azure-dash -- printenv | grep ^AZURE_
kubectl -n azure-dash get ingress azure-dash   # browse to the ADDRESS, or: kubectl -n azure-dash port-forward svc/azure-dash 8080
```

Open **Identity**. You should see:
- mode `workload-identity`
- federated token `sub = system:serviceaccount:azure-dash:azure-dash`, with `iss` = `$ISSUER`
- an Entra access token whose `xms_mirid` names `id-azure-dash`

**IMDS** shows the node's VM scale set in the `MC_…` resource group.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity record) | The FIC subject must be exactly `system:serviceaccount:azure-dash:azure-dash`, and the issuer must match `$ISSUER`, including the trailing slash. A new FIC can take a few minutes to propagate. |
| Mode is `dev`, not `workload-identity` | The pod was created before the label/annotation existed. Run `kubectl rollout restart`. Check `kubectl get pod -o yaml` for the injected `AZURE_*` env. |
| `AuthorizationFailed (403)` in panels | The Reader role assignment is missing or still propagating (up to about 10 minutes). |
| IMDS unavailable | Expected if the cluster uses `--enable-imds-restriction`. |
````

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add Kustomize base and AKS Workload ID overlay"
```

---

### Task 12: OpenShift overlays — client secret and native service-account issuer

**Files:**
- Create: `deploy/openshift/{kustomization,route,secret.example}.yaml`, `deploy/openshift/README.md`
- Create: `deploy/openshift-wi/{kustomization,route,identity-configmap}.yaml`, `deploy/openshift-wi/README.md`

**Interfaces:**
- Consumes: the Task 11 base, whose patch points are `/spec/template/spec/containers/0/{env,envFrom,volumeMounts}/-` and `/spec/template/spec/volumes/-`.
- Produces:
  - Secret `azure-dash-sp` (keys `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`), mounted as optional `envFrom`.
  - ConfigMap `azure-dash-identity` (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`).
  - A projected token at `/var/run/secrets/azure/tokens/azure-identity-token`.

- [ ] **Step 1: Write the client-secret overlay**

`deploy/openshift/route.yaml`. The same file is copied into `deploy/openshift-wi/` and `deploy/ztwim/`, because kustomize won't load files from outside an overlay's own directory.
```yaml
apiVersion: route.openshift.io/v1
kind: Route
metadata:
  name: azure-dash
spec:
  to:
    kind: Service
    name: azure-dash
  port:
    targetPort: http
  tls:
    termination: edge
    insecureEdgeTerminationPolicy: Redirect
```

`deploy/openshift/kustomization.yaml`:
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: azure-dash
resources:
  - ../kubernetes
  - route.yaml
patches:
  - target: {kind: Deployment, name: azure-dash}
    patch: |-
      - op: add
        path: /spec/template/spec/containers/0/envFrom/-
        value:
          secretRef:
            name: azure-dash-sp
            optional: true
```

`deploy/openshift/secret.example.yaml`. It isn't listed in the kustomization; copy it to `secret.yaml`, which is gitignored, and fill it in.
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: azure-dash-sp
  namespace: azure-dash
type: Opaque
stringData:
  AZURE_TENANT_ID: "00000000-0000-0000-0000-000000000000"
  AZURE_CLIENT_ID: "00000000-0000-0000-0000-000000000000"
  AZURE_CLIENT_SECRET: "replace-me"
```

`deploy/openshift/README.md`:
````markdown
# azure-dash on OpenShift (or any Kubernetes) with a client secret

The baseline to compare against Workload ID: a long-lived service principal secret stored in a Kubernetes Secret.
`AUTH_MODE=auto` selects `ClientSecretCredential` because `AZURE_CLIENT_SECRET` is present.

```bash
export SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az ad sp create-for-rbac --name azure-dash --role Reader --scopes "/subscriptions/$SUBSCRIPTION_ID" \
  --query '{AZURE_TENANT_ID:tenant, AZURE_CLIENT_ID:appId, AZURE_CLIENT_SECRET:password}' -o json
cp deploy/openshift/secret.example.yaml deploy/openshift/secret.yaml   # paste the three values; secret.yaml is gitignored
oc apply -k deploy/openshift
oc apply -f deploy/openshift/secret.yaml
oc -n azure-dash rollout restart deploy/azure-dash
oc -n azure-dash get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page shows mode `client-secret`, no federated token, and an Entra token whose `appid` is the service principal.
Rotate or delete the secret when you are done (`az ad sp delete --id <appId>`). Removing this secret is the
whole point of the Workload ID overlays.
````

Run: `kubectl kustomize deploy/openshift | grep -A3 'secretRef'`
Expected: `name: azure-dash-sp` and `optional: true`.

- [ ] **Step 2: Write the native-issuer overlay**

Copy the route: `cp deploy/openshift/route.yaml deploy/openshift-wi/route.yaml`

`deploy/openshift-wi/identity-configmap.yaml`:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: azure-dash-identity
data:
  AZURE_CLIENT_ID: "00000000-0000-0000-0000-000000000000"   # user-assigned managed identity client ID
  AZURE_TENANT_ID: "00000000-0000-0000-0000-000000000000"
```

`deploy/openshift-wi/kustomization.yaml`:
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: azure-dash
resources:
  - ../kubernetes
  - route.yaml
  - identity-configmap.yaml
patches:
  # Project a service-account token for Entra ourselves, so this works with or without the pod-identity webhook.
  - target: {kind: Deployment, name: azure-dash}
    patch: |-
      - op: add
        path: /spec/template/spec/volumes/-
        value:
          name: azure-identity-token
          projected:
            sources:
              - serviceAccountToken:
                  path: azure-identity-token
                  audience: api://AzureADTokenExchange
                  expirationSeconds: 3600
      - op: add
        path: /spec/template/spec/containers/0/volumeMounts/-
        value:
          name: azure-identity-token
          mountPath: /var/run/secrets/azure/tokens
          readOnly: true
      - op: add
        path: /spec/template/spec/containers/0/envFrom/-
        value:
          configMapRef:
            name: azure-dash-identity
      - op: add
        path: /spec/template/spec/containers/0/env/-
        value:
          name: AZURE_FEDERATED_TOKEN_FILE
          value: /var/run/secrets/azure/tokens/azure-identity-token
      - op: add
        path: /spec/template/spec/containers/0/env/-
        value:
          name: AZURE_AUTHORITY_HOST
          value: https://login.microsoftonline.com/
```

Run: `kubectl kustomize deploy/openshift-wi | grep -E 'audience: api://AzureADTokenExchange|AZURE_FEDERATED_TOKEN_FILE|azure-dash-identity|kind: Route'`
Expected: every pattern matches at least once. `azure-dash-identity` appears twice: once for the ConfigMap and once for the `configMapRef`.

- [ ] **Step 3: Write `deploy/openshift-wi/README.md`**

````markdown
# azure-dash on OpenShift using the cluster's own service-account issuer

Same mechanism as AKS Workload ID, using OpenShift's service-account issuer instead of AKS's: the pod gets a
projected token with audience `api://AzureADTokenExchange`, and Entra trusts it through a federated identity
credential. azure-dash runs `WorkloadIdentityCredential` (`AUTH_MODE=auto` sees `AZURE_FEDERATED_TOKEN_FILE`).
This overlay projects the token itself, so it does not depend on the pod-identity webhook.

**Requirement:** Entra must be able to fetch the issuer's discovery document and JWKS over public HTTPS.

## 1. Find the issuer and check it is publicly discoverable

```bash
export ISSUER=$(oc get authentication cluster -o jsonpath='{.spec.serviceAccountIssuer}')
echo "$ISSUER"
curl -fsS "$ISSUER/.well-known/openid-configuration" | jq '{issuer, jwks_uri}'
curl -fsS "$(curl -fsS "$ISSUER/.well-known/openid-configuration" | jq -r .jwks_uri)" | jq '.keys[] | {kty, alg, use}'
```

- **Azure Red Hat OpenShift (ARO)** with managed/workload identity: the issuer is `https://<region>.oic.aro.azure.net/...` and is public.
- **Self-managed OCP installed in Manual/STS mode with `ccoctl azure create-all`**: the issuer points to a public Azure Blob container.
- **Default OCP install**: `serviceAccountIssuer` is empty, which means `https://kubernetes.default.svc`. That isn't public, so Entra can't use it. Use the ZTWIM overlay (`deploy/ztwim`) instead, or re-issue with `ccoctl`.

## 2. Identity, federated credential, Reader

```bash
export RESOURCE_GROUP=rg-azure-dash IDENTITY=id-azure-dash-ocp
export SUBSCRIPTION_ID=$(az account show --query id -o tsv) TENANT_ID=$(az account show --query tenantId -o tsv)
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
az identity federated-credential create --name azure-dash-ocp --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$ISSUER" --subject system:serviceaccount:azure-dash:azure-dash --audience api://AzureADTokenExchange
az role assignment create --assignee-object-id "$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)" \
  --assignee-principal-type ServicePrincipal --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

## 3. Deploy

Put `$CLIENT_ID` and `$TENANT_ID` into `deploy/openshift-wi/identity-configmap.yaml`, then:

```bash
oc apply -k deploy/openshift-wi
oc -n azure-dash rollout status deploy/azure-dash
oc -n azure-dash get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page should show mode `workload-identity`, with a federated `iss` equal to `$ISSUER` and
`sub = system:serviceaccount:azure-dash:azure-dash`. If Entra rejects the token (`AADSTS700211`, no matching
federated identity record for the issuer), the issuer string in the FIC doesn't exactly match the token's `iss`.
````

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add OpenShift client-secret and native issuer overlays"
```

---

### Task 13: OpenShift ZTWIM (SPIFFE/SPIRE) overlay

**Files:**
- Create: `deploy/ztwim/{kustomization,route,identity-configmap,spiffe-helper-configmap}.yaml`, `deploy/ztwim/README.md`

**Interfaces:**
- Consumes: the Task 11 base, and `SPIFFE_JWT_FILE` with `AUTH_MODE=spiffe` (Tasks 1 and 7).
- Produces:
  - Namespace `azure-dash-ztwim`.
  - spiffe-helper runs as an init container (one shot) and as a sidecar (daemon). It writes the JWT-SVID to `/var/run/secrets/azure/token` on a shared in-memory emptyDir.
  - The SPIRE agent socket comes from the `csi.spiffe.io` CSI volume at `/run/spire/sockets`.

- [ ] **Step 1: Write the overlay**

Copy the route: `cp deploy/openshift/route.yaml deploy/ztwim/route.yaml`

`deploy/ztwim/identity-configmap.yaml`:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: azure-dash-identity
data:
  AZURE_CLIENT_ID: "00000000-0000-0000-0000-000000000000"   # user-assigned managed identity client ID
  AZURE_TENANT_ID: "00000000-0000-0000-0000-000000000000"
```

`deploy/ztwim/spiffe-helper-configmap.yaml`:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: spiffe-helper
data:
  # Plain spiffe-helper config; no shell templating. The audience must match the Entra federated credential.
  helper.conf: |
    agent_address = "/run/spire/sockets/spire-agent.sock"
    jwt_svids = [{jwt_audience = "api://AzureADTokenExchange", jwt_svid_file_name = "/var/run/secrets/azure/token"}]
    jwt_svid_file_mode = 0644
```

`deploy/ztwim/kustomization.yaml`:
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: azure-dash-ztwim
resources:
  - ../kubernetes
  - route.yaml
  - identity-configmap.yaml
  - spiffe-helper-configmap.yaml
patches:
  - target: {kind: Namespace, name: azure-dash}
    patch: |-
      - op: replace
        path: /metadata/name
        value: azure-dash-ztwim
  - target: {kind: Deployment, name: azure-dash}
    patch: |-
      - op: add
        path: /spec/template/spec/volumes/-
        value:
          name: spiffe-workload-api
          csi:
            driver: csi.spiffe.io
            readOnly: true
      - op: add
        path: /spec/template/spec/volumes/-
        value:
          name: spiffe-helper-config
          configMap:
            name: spiffe-helper
      - op: add
        path: /spec/template/spec/volumes/-
        value:
          name: azure-token
          emptyDir:
            medium: Memory
      - op: add
        path: /spec/template/spec/initContainers/-
        value:
          name: spiffe-helper-init
          image: registry.redhat.io/zero-trust-workload-identity-manager/spiffe-helper-rhel9:v0.11.0-1787560110
          command: ["/spiffe-helper"]
          args: ["-config", "/etc/spiffe-helper/helper.conf", "-daemon-mode=false"]
          securityContext:
            allowPrivilegeEscalation: false
            capabilities: {drop: [ALL]}
          volumeMounts:
            - {name: spiffe-workload-api, mountPath: /run/spire/sockets, readOnly: true}
            - {name: spiffe-helper-config, mountPath: /etc/spiffe-helper, readOnly: true}
            - {name: azure-token, mountPath: /var/run/secrets/azure}
      - op: add
        path: /spec/template/spec/containers/-
        value:
          name: spiffe-helper
          image: registry.redhat.io/zero-trust-workload-identity-manager/spiffe-helper-rhel9:v0.11.0-1787560110
          command: ["/spiffe-helper"]
          args: ["-config", "/etc/spiffe-helper/helper.conf", "-daemon-mode=true"]
          resources:
            requests: {cpu: 10m, memory: 32Mi}
            limits: {cpu: 100m, memory: 64Mi}
          securityContext:
            allowPrivilegeEscalation: false
            capabilities: {drop: [ALL]}
          volumeMounts:
            - {name: spiffe-workload-api, mountPath: /run/spire/sockets, readOnly: true}
            - {name: spiffe-helper-config, mountPath: /etc/spiffe-helper, readOnly: true}
            - {name: azure-token, mountPath: /var/run/secrets/azure}
      - op: add
        path: /spec/template/spec/containers/0/volumeMounts/-
        value:
          name: azure-token
          mountPath: /var/run/secrets/azure
          readOnly: true
      - op: add
        path: /spec/template/spec/containers/0/envFrom/-
        value:
          configMapRef:
            name: azure-dash-identity
      - op: add
        path: /spec/template/spec/containers/0/env/-
        value:
          name: AUTH_MODE
          value: spiffe
      - op: add
        path: /spec/template/spec/containers/0/env/-
        value:
          name: SPIFFE_JWT_FILE
          value: /var/run/secrets/azure/token
```

Run:
```bash
kubectl kustomize deploy/ztwim > /tmp/ztwim.yaml
grep -A2 '^kind: Namespace' /tmp/ztwim.yaml
grep -c 'namespace: azure-dash-ztwim' /tmp/ztwim.yaml
grep -E 'driver: csi.spiffe.io|name: spiffe-helper-init|SPIFFE_JWT_FILE|value: spiffe$' /tmp/ztwim.yaml
```
Expected:
- The Namespace block shows `name: azure-dash-ztwim`.
- The count is 7: the ServiceAccount, 3 ConfigMaps, the Deployment, the Service and the Route.
- All four patterns match.

If the Namespace block comes out empty because kustomize already renamed it, drop the Namespace patch, since the namespace transformer already handles it, and re-run.

- [ ] **Step 2: Write `deploy/ztwim/README.md`**

````markdown
# azure-dash on OpenShift with Zero Trust Workload Identity Manager (SPIFFE/SPIRE) → Azure

SPIRE issues the pod a **JWT-SVID** whose `sub` is its SPIFFE ID. Entra trusts SPIRE's OIDC discovery endpoint
through a federated identity credential, and exchanges the JWT-SVID for an access token.

- spiffe-helper (Red Hat image) fetches the JWT-SVID over the SPIRE agent socket. The socket is mounted with the `csi.spiffe.io` CSI driver.
- spiffe-helper writes the JWT-SVID to `/var/run/secrets/azure/token`.
- azure-dash uses `ClientAssertionCredential` and **re-reads that file on every token exchange**. ZTWIM's default JWT-SVID lifetime is about 5 minutes, so a cached copy would expire.

## 0. ZTWIM prerequisites Entra enforces

The Zero Trust Workload Identity Manager operator must be installed, with its `cluster` CRs created. Entra has three requirements:

1. **RS256 signing.** Entra only accepts RSA-signed tokens, while upstream SPIRE defaults to EC keys:
   ```yaml
   apiVersion: operator.openshift.io/v1alpha1
   kind: SpireServer
   metadata: {name: cluster}
   spec:
     jwtIssuer: https://oidc-discovery.apps.<cluster-domain>
     jwtKeyType: rsa-2048
     # ...
   ```
2. **Public discovery endpoint.** `SpireOIDCDiscoveryProvider` must be reachable from the internet over HTTPS with a **publicly trusted** certificate, for example `managedRoute: "true"` plus a certificate from cert-manager/ACME via `externalSecretRef`. Its `jwtIssuer` must equal the SpireServer's.
3. **Key usage.** The keys in the JWKS must carry `"use": "sig"`.

Check all three:

```bash
export JWT_ISSUER=$(oc get spireserver cluster -o jsonpath='{.spec.jwtIssuer}')
export TRUST_DOMAIN=$(oc get zerotrustworkloadidentitymanager cluster -o jsonpath='{.spec.trustDomain}')
curl -fsS "$JWT_ISSUER/.well-known/openid-configuration" | jq '{issuer, jwks_uri, id_token_signing_alg_values_supported}'
curl -fsS "$(curl -fsS "$JWT_ISSUER/.well-known/openid-configuration" | jq -r .jwks_uri)" | jq '.keys[] | {kty, alg, use, kid}'
```

**Expect `kty: "RSA"` and `use: "sig"`.** Run these from outside the cluster to prove the endpoint is public.

Workloads need a SPIFFE ID of the form `spiffe://<trust-domain>/ns/<namespace>/sa/<service-account>`. Check which `ClusterSPIFFEID` resources exist with `oc get clusterspiffeid`. If none matches, create one:

```yaml
apiVersion: spire.spiffe.io/v1alpha1
kind: ClusterSPIFFEID
metadata: {name: azure-dash}
spec:
  spiffeIDTemplate: "spiffe://{{ .TrustDomain }}/ns/{{ .PodMeta.Namespace }}/sa/{{ .PodSpec.ServiceAccountName }}"
  namespaceSelector:
    matchLabels: {kubernetes.io/metadata.name: azure-dash-ztwim}
```

## 1. Identity, federated credential, Reader

```bash
export RESOURCE_GROUP=rg-azure-dash IDENTITY=id-azure-dash-ztwim
export SUBSCRIPTION_ID=$(az account show --query id -o tsv) TENANT_ID=$(az account show --query tenantId -o tsv)
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
az identity federated-credential create --name azure-dash-ztwim --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$JWT_ISSUER" \
  --subject "spiffe://$TRUST_DOMAIN/ns/azure-dash-ztwim/sa/azure-dash" \
  --audience api://AzureADTokenExchange
az role assignment create --assignee-object-id "$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)" \
  --assignee-principal-type ServicePrincipal --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

## 2. Deploy

Put `$CLIENT_ID` and `$TENANT_ID` into `deploy/ztwim/identity-configmap.yaml`, then:

```bash
oc apply -k deploy/ztwim
oc -n azure-dash-ztwim rollout status deploy/azure-dash
oc -n azure-dash-ztwim logs deploy/azure-dash -c spiffe-helper --tail=20
oc -n azure-dash-ztwim exec deploy/azure-dash -c azure-dash -- head -c 20 /var/run/secrets/azure/token; echo   # expect: eyJ...
oc -n azure-dash-ztwim get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page should show:
- mode `spiffe`, credential `ClientAssertionCredential`
- a federated `sub` of `spiffe://<trust-domain>/ns/azure-dash-ztwim/sa/azure-dash` and an `iss` of `$JWT_ISSUER`
- an Entra token whose `xms_mirid` names `id-azure-dash-ztwim`

Wait more than 5 minutes, then **Refresh all** on the Azure page. The panels still load, which shows that the rotated JWT-SVID is picked up.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `SPIFFE JWT-SVID file not found … spiffe-helper` | The init container failed. Check `oc logs deploy/azure-dash -c spiffe-helper-init`. The CSI driver or the `ClusterSPIFFEID` is missing. |
| `AADSTS700211` (no matching federated identity record for the issuer) | The FIC `--issuer` must exactly equal the token's `iss`, i.e. `$JWT_ISSUER`, including scheme and trailing slash. |
| `AADSTS700213` (… for the subject) | The FIC `--subject` must equal the SPIFFE ID shown on the Identity page. |
| Signature or key errors | `jwtKeyType` is not RSA, or the JWKS lacks `use: sig`. Fix the SpireServer CR, then wait for key rotation. |
| Entra can't fetch the keys | The discovery route isn't public, or its certificate isn't publicly trusted. |
````

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: add OpenShift ZTWIM (SPIFFE/SPIRE) overlay"
```

---

### Task 14: CI and Dependabot

**Files:**
- Create: `.github/workflows/build-container.yaml`, `.github/dependabot.yml`

**Interfaces:**
- Consumes: `global.json`, the test project and `Containerfile` from earlier tasks.
- Produces:
  - Image tags on `ghcr.io/<owner>/azure-dash`: `latest` (default branch), branch, tag, `sha-<short>`, the long SHA, and `schedule` (weekly rebuild).
  - PRs build the image but don't push it.

- [ ] **Step 1: Write the workflow**

`.github/workflows/build-container.yaml`:
```yaml
name: build-container

on:
  push:
    branches: [main]
    tags: ["v*"]
    paths-ignore: ["deploy/**", "docs/**", "**/*.md"]
  pull_request:
    paths-ignore: ["deploy/**", "docs/**", "**/*.md"]
  schedule:
    - cron: "0 2 * * 0"
  workflow_dispatch: {}

permissions:
  contents: read
  packages: write

env:
  IMAGE: ghcr.io/${{ github.repository_owner }}/azure-dash

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json
      - run: dotnet test --configuration Release

  build:
    needs: test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        if: github.event_name != 'pull_request'
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - id: meta
        uses: docker/metadata-action@v5
        with:
          images: ${{ env.IMAGE }}
          tags: |
            type=schedule
            type=ref,event=branch
            type=ref,event=tag
            type=ref,event=pr
            type=sha
            type=sha,format=long
            type=raw,value=latest,enable={{is_default_branch}}
      # The SDK stage cross-compiles on the runner's platform and the final stage has no RUN, so no QEMU is needed.
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: Containerfile
          platforms: linux/amd64,linux/arm64
          push: ${{ github.event_name != 'pull_request' }}
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

`.github/dependabot.yml`:
```yaml
version: 2
updates:
  - package-ecosystem: nuget
    directory: /
    schedule: {interval: weekly}
    groups:
      nuget-minor-patch:
        update-types: [minor, patch]
  - package-ecosystem: docker
    directory: /
    schedule: {interval: weekly}
  - package-ecosystem: github-actions
    directory: /
    schedule: {interval: weekly}
```

- [ ] **Step 2: Lint the workflow**

```bash
podman run --rm -v "$PWD":/repo:Z -w /repo docker.io/rhysd/actionlint:latest -color
```
Expected: no output and exit code 0.

- [ ] **Step 3: Check the multi-arch build locally (arm64 host → amd64 target)**

```bash
podman build --platform linux/amd64 --format docker -t localhost/azure-dash:amd64 .
podman image inspect localhost/azure-dash:amd64 --format '{{.Architecture}}'
```
Expected: `amd64`. The build should succeed without emulating the SDK stage: it runs `--platform=$BUILDPLATFORM` and cross-compiles with `-a amd64`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "ci: add multi-arch container build with tests and Dependabot"
```

---

### Task 15: READMEs and final verification

**Files:**
- Create: `README.md`, `deploy/README.md`

**Interfaces:**
- Consumes: everything above. The README documents routes, env vars and overlays exactly as the earlier tasks built them.

- [ ] **Step 1: Write `deploy/README.md`**

````markdown
# Deploying azure-dash

| Overlay | Platform | Credential | Secret in cluster? |
| --- | --- | --- | --- |
| [`aks/`](aks/README.md) | AKS | `WorkloadIdentityCredential` (projected SA token → Entra, via the Workload ID webhook) | No |
| [`openshift-wi/`](openshift-wi/README.md) | ARO / OCP with a public SA issuer | `WorkloadIdentityCredential` (token projected by the overlay) | No |
| [`ztwim/`](ztwim/README.md) | OpenShift + Zero Trust Workload Identity Manager | `ClientAssertionCredential` (SPIFFE JWT-SVID → Entra) | No |
| [`openshift/`](openshift/README.md) | Any Kubernetes/OpenShift | `ClientSecretCredential` | **Yes** (baseline for comparison) |

`kubernetes/` is the shared base: namespace `azure-dash`, ServiceAccount, ConfigMap `azure-dash-config`,
Deployment and Service on port 8080.

## Azure prerequisites shared by every overlay

1. **An identity.** Use a user-assigned managed identity for the federated overlays; it is single-tenant and
   managed with Azure RBAC. Use an app registration (service principal) for the client-secret overlay.
2. **Read access** for that identity:
   - `Reader` on the subscription, or
   - `Reader` on one resource group, with `AZURE_SUBSCRIPTION_ID` and `AZURE_RESOURCE_GROUP` set in the ConfigMap. Listing subscriptions returns nothing at resource-group scope.

   New role assignments can take up to 10 minutes to apply.
3. **A federated identity credential** (federated overlays only) whose issuer, subject and audience
   (`api://AzureADTokenExchange`) match the token the pod presents. An identity can hold up to 20 of these.

| Overlay | FIC issuer | FIC subject |
| --- | --- | --- |
| aks | `az aks show --query oidcIssuerProfile.issuerUrl` | `system:serviceaccount:azure-dash:azure-dash` |
| openshift-wi | `oc get authentication cluster -o jsonpath='{.spec.serviceAccountIssuer}'` | `system:serviceaccount:azure-dash:azure-dash` |
| ztwim | `oc get spireserver cluster -o jsonpath='{.spec.jwtIssuer}'` | `spiffe://<trust-domain>/ns/azure-dash-ztwim/sa/azure-dash` |
````

- [ ] **Step 2: Write `README.md`**

````markdown
# azure-dash

A small .NET 10 dashboard for demonstrating **Microsoft Entra Workload ID** on AKS and **federation from OpenShift**
(the cluster's own issuer, or Zero Trust Workload Identity Manager / SPIFFE). It is the Azure counterpart of
[gcp-dash](https://github.com/kenmoini/gcp-dash).

- **Runtime:** container, pod and cgroup info, plus controls to fail liveness or readiness, burn CPU, or crash the process.
- **Azure:** the subscription, resource groups, VMs and scale sets, storage accounts, VNets, subnets, NSG rules, and a Resource Graph count of resources by type. Each panel is cached and fails on its own.
- **Identity:** which credential the app chose and why, the decoded federated token the cluster presented (iss/sub/aud/exp), and the decoded Entra access token it got back (oid/tid/appid/xms_mirid). Raw tokens are never shown. It also shows **IMDS**: the node's VM metadata, when reachable.

## Authentication modes

`AUTH_MODE=auto` (the default) picks the first one that matches. `DefaultAzureCredential` is deliberately not used.

| Mode | Selected when | Credential |
| --- | --- | --- |
| `workload-identity` | `AZURE_FEDERATED_TOKEN_FILE` is set (AKS webhook, or the `openshift-wi` overlay) | `WorkloadIdentityCredential` |
| `spiffe` | `SPIFFE_JWT_FILE` is set (`ztwim` overlay) | `ClientAssertionCredential`, re-reading the JWT-SVID on every exchange |
| `client-secret` | `AZURE_CLIENT_SECRET` is set | `ClientSecretCredential` |
| `dev` | none of the above | Azure CLI, then Azure Developer CLI |

## Run locally

```bash
az login
dotnet test
ASPNETCORE_HTTP_PORTS=8080 dotnet run --project src/AzureDash    # http://localhost:8080
```

Container:

```bash
podman build --format docker -t localhost/azure-dash:dev .   # --format docker keeps the HEALTHCHECK
AZURE_TENANT_ID=... AZURE_CLIENT_ID=... AZURE_CLIENT_SECRET=... scripts/run-podman.sh
```

## Deploy

See [deploy/README.md](deploy/README.md). The overlays are `deploy/aks`, `deploy/openshift-wi`, `deploy/ztwim` and `deploy/openshift`.

## Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/`, `/azure`, `/identity` | Pages |
| GET | `/healthz/live`, `/healthz/ready` | `200 {"status":"ok"}`, or `503` when turned off from the controls |
| POST | `/controls/liveness`, `/controls/readiness` | Form `enabled=true\|false` |
| POST | `/controls/cpu` | Form `enabled`, `workers` (1–64, capped at the visible CPUs) |
| POST | `/controls/crash` | Form `exit_code` (0–255). Returns 202, then exits after 0.5 s. |
| GET | `/api/state`, `/api/runtime` | JSON |
| GET | `/api/azure/{kind}[?refresh=true]` | `kind` is one of `subscription`, `resourcegroups`, `vms`, `storageaccounts`, `vnets`, `subnets`, `nsgrules`, `resourcegraph`. Returns `{kind, fetched_at, error, items}`. |
| POST | `/api/azure/refresh` | Clears the cache (204) |
| GET | `/api/identity`, `/api/imds` | JSON |
| GET | `/partials/*` | htmx fragments |

Every `/controls/*` endpoint returns 403 when `CONTROLS_ENABLED=false`. Control and fragment endpoints return HTML to htmx (`HX-Request: true`) and JSON otherwise.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `AUTH_MODE` | `auto` | `auto`, `workload-identity`, `spiffe`, `client-secret` or `dev` |
| `AZURE_SUBSCRIPTION_ID` | — | Subscription to show. If unset, the app uses the only one it can see, or the first one plus a warning. |
| `AZURE_RESOURCE_GROUP` | — | Limit every panel to one resource group, for least-privilege Reader |
| `AZURE_CLOUD` | `public` | `public`, `usgov` or `china`. Sets the authority host, the ARM endpoint and the token scope. |
| `AZURE_CACHE_TTL_SECONDS` | `60` | How long panel data is cached |
| `CONTROLS_ENABLED` | `true` | Set to `false` on shared clusters |
| `LOG_LEVEL` | `Information` | `Trace` … `None` |
| `AZURE_DEBUG` | `false` | Log Azure SDK HTTP, retry and token events. Authorization headers are redacted by Azure.Core. |
| `SPIFFE_JWT_FILE` | — | Path to the JWT-SVID written by spiffe-helper |
| `IMDS_ENABLED` / `IMDS_API_VERSION` | `true` / `2021-02-01` | Instance Metadata Service lookup (1 s timeout) |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE`, `AZURE_AUTHORITY_HOST`, `AZURE_CLIENT_SECRET` | — | Standard Azure Identity variables. The AKS webhook injects the first four. |
| `POD_NAME`, `POD_NAMESPACE`, `NODE_NAME`, `POD_IP`, `SERVICE_ACCOUNT` | — | Downward API, set by the base Deployment |

## Demo script

1. **Identity:** show the federated `iss`/`sub` and the Entra `oid`. There's no secret anywhere in the namespace.
2. **Azure:** the panels load. Remove the Reader role assignment and **Refresh all**: every panel shows a 403 with the "grant Reader" hint while keeping its last data. Restore the role; after propagation the panels recover.
3. **Readiness off:** `kubectl get endpoints azure-dash` drops the pod, and the Route or Ingress returns 503.
4. **Liveness off:** after about 15 s the kubelet restarts the container and the flags reset.
5. **CPU load:** `kubectl top pod`, or watch an HPA scale out.
6. **Crash** with exit code 3: `kubectl get pod` shows the restart count increase and `lastState.terminated.exitCode: 3`.

## Security

Everything is unauthenticated and the controls have no CSRF protection. That's fine for a demo, but don't expose
it publicly, set `CONTROLS_ENABLED=false` on shared clusters, and grant only `Reader`.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `AADSTS700211` | The federated credential's issuer doesn't match the token's `iss`. Compare them on the Identity page. |
| `AADSTS700213` / `AADSTS70021` | The subject doesn't match: check namespace, service account or SPIFFE ID. A new FIC can take a few minutes to propagate. |
| `AuthorizationFailed (403)` | Reader is missing on the scope, or still propagating (up to about 10 minutes). |
| "No subscriptions are visible…" | Reader is scoped to a resource group. Set `AZURE_SUBSCRIPTION_ID` and `AZURE_RESOURCE_GROUP`. |
| Mode is `dev` in a cluster | Workload ID env wasn't injected: missing pod label, or the pod predates the annotation. Restart it. |
| IMDS unavailable | Not on an Azure VM, or AKS `--enable-imds-restriction`. This is expected. |

For more detail, run `kubectl -n azure-dash set env deploy/azure-dash AZURE_DEBUG=true` and read the logs.
````

- [ ] **Step 3: Final verification**

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build -c Release 2>&1 | tail -3
dotnet test -c Release 2>&1 | tail -3
for o in kubernetes aks openshift openshift-wi ztwim; do kubectl kustomize deploy/$o > /dev/null && echo "$o ok"; done
podman build --format docker -t localhost/azure-dash:dev . >/dev/null && echo "image ok"
podman run --rm -v "$PWD":/repo:Z -w /repo docker.io/rhysd/actionlint:latest && echo "actionlint ok"
git status --short
```
Expected:
- `Build succeeded` with 0 warnings
- `Passed!` with 0 failed
- `kubernetes ok`, `aks ok`, `openshift ok`, `openshift-wi ok`, `ztwim ok`
- `image ok`, `actionlint ok`
- A clean working tree once the READMEs are committed

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: add README and deployment guide"
```

- [ ] **Step 5: Live checks (need clusters; the human partner runs or supervises these)**

- AKS: follow `deploy/aks/README.md`, then walk through the demo script. The Identity page should show mode `workload-identity`, and IMDS should show the `MC_…` resource group.
- OpenShift ZTWIM: follow `deploy/ztwim/README.md`. After more than 5 minutes, **Refresh all** still succeeds, which proves the JWT-SVID is re-read.
- OpenShift native issuer, if the cluster's issuer is public: follow `deploy/openshift-wi/README.md`.
