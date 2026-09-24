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
