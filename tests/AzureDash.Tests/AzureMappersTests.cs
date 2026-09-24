using AzureDash.Inventory;

namespace AzureDash.Tests;

public class AzureMappersTests
{
    [Fact]
    public void Power_state_from_instance_view_codes()
    {
        Assert.Equal("running", AzureMappers.PowerState(["ProvisioningState/succeeded", "PowerState/running"]));
        Assert.Equal("deallocated", AzureMappers.PowerState(["powerstate/deallocated"]));
        Assert.Null(AzureMappers.PowerState(["ProvisioningState/succeeded"]));
        Assert.Null(AzureMappers.PowerState(null));
    }

    [Fact]
    public void Join_skips_blanks_and_returns_null_when_empty()
    {
        Assert.Equal("1, 3", AzureMappers.Join(["1", "", null, "3"]));
        Assert.Null(AzureMappers.Join([]));
        Assert.Null(AzureMappers.Join(null));
    }

    [Fact]
    public void One_or_many_prefers_list_then_single_then_star()
    {
        Assert.Equal("80, 443", AzureMappers.OneOrMany("22", ["80", "443"]));
        Assert.Equal("22", AzureMappers.OneOrMany("22", []));
        Assert.Equal("*", AzureMappers.OneOrMany(null, null));
    }

    [Fact]
    public void Kql_string_escapes_quotes_and_backslashes()
    {
        Assert.Equal("'rg-demo'", AzureMappers.KqlString("rg-demo"));
        Assert.Equal(@"'a\'b\\c'", AzureMappers.KqlString(@"a'b\c"));
    }

    [Fact]
    public void Parses_resource_graph_object_array()
    {
        var rows = AzureMappers.ParseGraphRows(BinaryData.FromString(
            "[{\"type\":\"microsoft.compute/virtualmachines\",\"count_\":3},{\"type\":\"microsoft.network/virtualnetworks\",\"count_\":1}]"));
        Assert.Equal(new[] { new ResourceTypeCount("microsoft.compute/virtualmachines", 3), new ResourceTypeCount("microsoft.network/virtualnetworks", 1) }, rows);
    }

    [Fact]
    public void Resource_graph_non_array_is_an_error()
    {
        Assert.Throws<AzureError>(() => AzureMappers.ParseGraphRows(BinaryData.FromString("{\"columns\":[]}")));
    }

    [Fact]
    public void Power_states_by_id_matches_case_insensitively()
    {
        var byId = AzureMappers.PowerStatesById(
        [
            ("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/VM-A", ["PowerState/running"]),
        ]);
        Assert.Equal("running", byId["/subscriptions/s/resourcegroups/rg/providers/microsoft.compute/virtualmachines/vm-a"]);
    }

    [Fact]
    public void Power_states_by_id_missing_vm_has_no_entry()
    {
        var byId = AzureMappers.PowerStatesById(
        [
            ("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-a", ["PowerState/running"]),
        ]);
        Assert.False(byId.ContainsKey("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-b"));
        Assert.Null(byId.GetValueOrDefault("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-b"));
    }
}
