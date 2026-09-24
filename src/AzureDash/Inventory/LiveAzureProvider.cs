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
                d.HardwareProfile?.VmSize?.ToString(), AzureMappers.PowerState(d.InstanceView?.Statuses?.Select(s => s.Code)), null));
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
