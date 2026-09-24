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
