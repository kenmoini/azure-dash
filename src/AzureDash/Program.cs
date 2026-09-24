using System.Text.Json;
using AzureDash;
using AzureDash.Configuration;
using AzureDash.Endpoints;
using AzureDash.Identity;
using AzureDash.Imds;
using AzureDash.Inventory;
using AzureDash.Load;
using AzureDash.Runtime;
using AzureDash.State;

if (args.Contains("--healthcheck"))
    return await SelfProbe.RunAsync(SelfProbe.LocalUrl(Environment.GetEnvironmentVariable), new SocketsHttpHandler(), Console.Out);

EnvLookup env = Environment.GetEnvironmentVariable;
var settings = AppSettings.FromEnvironment(env);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(settings.LogLevel);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
if (settings.AzureDebug)
    builder.Logging.AddFilter("AzureDash", LogLevel.Debug).AddFilter("Azure.Sdk", LogLevel.Information);

builder.Services.AddSingleton(env);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton(_ => new CpuLoad());
builder.Services.AddSingleton<StateService>();
builder.Services.AddSingleton<IProcessExit, EnvironmentProcessExit>();
builder.Services.AddSingleton(sp => new RuntimeInfoProvider("/", sp.GetRequiredService<EnvLookup>(), sp.GetRequiredService<StateService>()));
builder.Services.AddSingleton(sp => new TtlCache(sp.GetRequiredService<AppSettings>().CacheTtl, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => CredentialProvider.FromSettings(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<EnvLookup>()));
builder.Services.AddSingleton<Func<IAzureProvider>>(sp => () =>
    new LiveAzureProvider(sp.GetRequiredService<CredentialProvider>(), sp.GetRequiredService<AppSettings>()));
builder.Services.AddSingleton<AzureSdkLogging>();
builder.Services.AddSingleton<AzureService>();
builder.Services.AddSingleton<IdentityInfoService>();
builder.Services.AddHttpClient(ImdsClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, ConnectTimeout = ImdsClient.Timeout });
builder.Services.AddSingleton(sp => new ImdsClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ImdsClient.HttpClientName),
    sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents();

var app = builder.Build();
if (settings.AzureDebug) app.Services.GetRequiredService<AzureSdkLogging>().Start();

app.UseStaticFiles();
app.MapRazorPages();

app.MapHealthEndpoints();
app.MapControlsEndpoints();
app.MapRuntimeEndpoints();
app.MapAzureEndpoints();
app.MapIdentityEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
