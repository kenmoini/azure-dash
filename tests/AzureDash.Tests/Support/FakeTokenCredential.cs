using Azure.Core;

namespace AzureDash.Tests.Support;

public sealed class FakeTokenCredential(Func<string> token) : TokenCredential
{
    public List<string[]> Requests { get; } = [];

    public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
    {
        Requests.Add(context.Scopes);
        return new AccessToken(token(), DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
        ValueTask.FromResult(GetToken(context, ct));
}
