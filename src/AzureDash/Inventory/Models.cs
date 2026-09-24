namespace AzureDash.Inventory;

public sealed record SubscriptionInfo(string Id, string DisplayName, string? State, string? TenantId, string Source, string? Warning);
public sealed record ResourceGroupInfo(string Name, string Location, string? ProvisioningState);
public sealed record VmInfo(string Kind, string Name, string ResourceGroup, string Location, string? Zones, string? Size, string? PowerState, int? Capacity);
public sealed record StorageAccountInfo(string Name, string ResourceGroup, string Location, string? Kind, string? Sku, string? AccessTier, string? PublicNetworkAccess);
public sealed record VnetInfo(string Name, string ResourceGroup, string Location, string AddressSpace, int SubnetCount);
public sealed record SubnetInfo(string Vnet, string Name, string? AddressPrefix, string? Nsg, string? RouteTable);
public sealed record NsgRuleInfo(string Nsg, string Rule, string? Direction, int? Priority, string? Access, string? Protocol, string Ports, string Source, string Destination);
public sealed record ResourceTypeCount(string Type, long Count);
