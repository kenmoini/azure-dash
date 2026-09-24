using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class JwtDisplayTests
{
    [Fact]
    public void Decodes_selected_claims_in_order_and_times()
    {
        var jwt = TestJwt.Make(new
        {
            iss = "https://eastus.oic.prod-aks.azure.com/t/u/",
            sub = "system:serviceaccount:azure-dash:azure-dash",
            aud = new[] { "api://AzureADTokenExchange" },
            iat = 1767225600,
            exp = 1767229200,
            secret_claim = "not requested",
        });
        var s = JwtDisplay.Decode(jwt, JwtDisplay.FederatedClaims);
        Assert.Null(s.Error);
        Assert.Equal(new[] { "iss", "sub", "aud", "iat", "exp" }, s.Claims.Keys);
        Assert.Equal("system:serviceaccount:azure-dash:azure-dash", s.Claims["sub"]);
        Assert.Equal("api://AzureADTokenExchange", s.Claims["aud"]);
        Assert.Equal("1767229200", s.Claims["exp"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), s.IssuedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767229200), s.ExpiresAt);
        Assert.DoesNotContain(s.Claims.Values, v => v.Contains(TestJwt.Signature));
    }

    [Fact]
    public void Absent_claims_are_skipped()
    {
        var s = JwtDisplay.Decode(TestJwt.Make(new { oid = "o", tid = "t", ver = "1.0", appid = "a" }), JwtDisplay.AccessTokenClaims);
        Assert.Equal(new[] { "oid", "tid", "appid", "ver" }, s.Claims.Keys);
        Assert.Null(s.ExpiresAt);
    }

    [Theory]
    [InlineData(null, "no token")]
    [InlineData("", "no token")]
    [InlineData("abc", "expected 3 dot-separated segments")]
    [InlineData("a.!!!.c", "could not decode token")]
    [InlineData("a.bm90IGpzb24.c", "could not decode token")]
    [InlineData("a.WzEsMl0.c", "payload is not a JSON object")]
    public void Malformed_tokens_return_error_not_exception(string? token, string message)
    {
        var s = JwtDisplay.Decode(token, JwtDisplay.FederatedClaims);
        Assert.Contains(message, s.Error);
        Assert.Empty(s.Claims);
    }
}
