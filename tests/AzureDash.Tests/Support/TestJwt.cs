using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace AzureDash.Tests.Support;

public static class TestJwt
{
    public const string Signature = "c2lnbmF0dXJl";

    public static string Make(object payload) =>
        $"{B64("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{B64(JsonSerializer.Serialize(payload))}.{Signature}";

    static string B64(string s) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(s));
}
