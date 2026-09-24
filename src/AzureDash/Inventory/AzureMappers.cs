using System.Text.Json;

namespace AzureDash.Inventory;

public static class AzureMappers
{
    const string PowerStatePrefix = "PowerState/";

    public static string? PowerState(IEnumerable<string?>? statusCodes) =>
        statusCodes?.FirstOrDefault(c => c?.StartsWith(PowerStatePrefix, StringComparison.OrdinalIgnoreCase) == true)?[PowerStatePrefix.Length..];

    public static string? Join(IEnumerable<string?>? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list is { Count: > 0 } ? string.Join(", ", list) : null;
    }

    public static string OneOrMany(string? single, IEnumerable<string?>? many) =>
        Join(many) ?? (string.IsNullOrWhiteSpace(single) ? "*" : single);

    public static string KqlString(string value) => "'" + value.Replace(@"\", @"\\").Replace("'", @"\'") + "'";

    public static IReadOnlyList<ResourceTypeCount> ParseGraphRows(BinaryData data)
    {
        using var doc = JsonDocument.Parse(data.ToMemory());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new AzureError("Resource Graph returned an unexpected result format (expected an object array)");
        return doc.RootElement.EnumerateArray()
            .Select(row => new ResourceTypeCount(
                row.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                row.TryGetProperty("count_", out var c) && c.TryGetInt64(out var n) ? n : 0))
            .ToList();
    }
}
