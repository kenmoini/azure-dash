using System.Globalization;
using AzureDash.Configuration;

namespace AzureDash.Endpoints;

/// <summary>Reads form + query values without [FromForm] binding (which would demand antiforgery tokens).</summary>
internal static class Input
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(HttpRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Query) values[key] = value.ToString();
        if (request.HasFormContentType)
            foreach (var (key, value) in await request.ReadFormAsync()) values[key] = value.ToString();
        return values;
    }

    public static bool? Bool(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? AppSettings.TryParseBool(v) : null;

    /// <summary>Returns <paramref name="fallback"/> when absent/blank, the value when in range, otherwise null.</summary>
    public static int? Int(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : null;
    }
}
