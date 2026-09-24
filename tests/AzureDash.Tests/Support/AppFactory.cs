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
