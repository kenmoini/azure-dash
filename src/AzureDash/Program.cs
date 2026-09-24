using System.Text.Json;
using AzureDash.Configuration;
using AzureDash.Endpoints;
using AzureDash.Load;
using AzureDash.Runtime;
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
builder.Services.AddSingleton(sp => new RuntimeInfoProvider("/", sp.GetRequiredService<EnvLookup>(), sp.GetRequiredService<StateService>()));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.MapHealthEndpoints();
app.MapControlsEndpoints();
app.MapRuntimeEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
