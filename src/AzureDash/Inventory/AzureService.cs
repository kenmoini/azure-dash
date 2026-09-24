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
