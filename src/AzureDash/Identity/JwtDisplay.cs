using System.Buffers.Text;
using System.Text.Json;

namespace AzureDash.Identity;

public sealed record JwtSummary(IReadOnlyDictionary<string, string> Claims, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, string? Error)
{
    public static JwtSummary Failed(string error) => new(new Dictionary<string, string>(), null, null, error);
}

/// <summary>Decodes (never verifies) a JWT payload and returns only the requested claims, for display.</summary>
public static class JwtDisplay
{
    public static readonly IReadOnlyList<string> FederatedClaims = ["iss", "sub", "aud", "iat", "exp"];
    public static readonly IReadOnlyList<string> AccessTokenClaims = ["aud", "iss", "oid", "tid", "appid", "azp", "idtyp", "ver", "xms_mirid", "exp"];

    public static JwtSummary Decode(string? token, IReadOnlyList<string> claims)
    {
        if (string.IsNullOrWhiteSpace(token)) return JwtSummary.Failed("no token");
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return JwtSummary.Failed($"could not decode token: expected 3 dot-separated segments, found {parts.Length}");
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return JwtSummary.Failed("could not decode token: payload is not a JSON object");
            var result = new Dictionary<string, string>();
            foreach (var name in claims)
                if (root.TryGetProperty(name, out var value)) result[name] = Render(value);
            return new JwtSummary(result, Epoch(root, "iat"), Epoch(root, "exp"), null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return JwtSummary.Failed($"could not decode token: {ex.Message}");
        }
    }

    static string Render(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Render)),
        _ => v.GetRawText(),
    };

    static DateTimeOffset? Epoch(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;
}
