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
