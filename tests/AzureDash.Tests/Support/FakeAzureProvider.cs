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
