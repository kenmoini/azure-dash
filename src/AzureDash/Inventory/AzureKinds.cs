namespace AzureDash.Inventory;

public static class AzureKinds
{
    public static readonly IReadOnlyList<(string Kind, string Title)> All =
    [
        ("subscription", "Subscription"),
        ("resourcegroups", "Resource groups"),
        ("vms", "Virtual machines & scale sets"),
        ("storageaccounts", "Storage accounts"),
        ("vnets", "Virtual networks"),
        ("subnets", "Subnets"),
        ("nsgrules", "Network security group rules"),
        ("resourcegraph", "Resource Graph: resources by type"),
    ];

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == kind);

    public static string Title(string kind) => All.First(k => k.Kind == kind).Title;
}
