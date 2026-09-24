using System.Text;
using AzureDash.Identity;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class SpiffeAssertionSourceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffe-").FullName;
    string PathOf(string name) => Path.Combine(_dir, name);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Reads_raw_jwt_and_trims_whitespace()
    {
        var jwt = TestJwt.Make(new { sub = "spiffe://td/ns/azure-dash-ztwim/sa/azure-dash" });
        await File.WriteAllTextAsync(PathOf("token"), jwt + "\n");
        Assert.Equal(jwt, await new SpiffeAssertionSource(PathOf("token")).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unwraps_base64_encoded_jwt()
    {
        var jwt = TestJwt.Make(new { sub = "x" });
        await File.WriteAllTextAsync(PathOf("token"), Convert.ToBase64String(Encoding.UTF8.GetBytes(jwt)));
        Assert.Equal(jwt, await new SpiffeAssertionSource(PathOf("token")).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Re_reads_the_file_every_call()
    {
        var source = new SpiffeAssertionSource(PathOf("token"));
        var first = TestJwt.Make(new { n = 1 });
        var second = TestJwt.Make(new { n = 2 });
        await File.WriteAllTextAsync(PathOf("token"), first);
        Assert.Equal(first, await source.ReadAsync(CancellationToken.None));
        await File.WriteAllTextAsync(PathOf("token"), second);
        Assert.Equal(second, await source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_file_names_path_and_sidecar()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new SpiffeAssertionSource(PathOf("absent")).ReadAsync(CancellationToken.None));
        Assert.Contains(PathOf("absent"), ex.Message);
        Assert.Contains("spiffe-helper", ex.Message);
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("   \n", "is empty")]
    [InlineData("not a token", "does not contain a JWT")]
    [InlineData("aGVsbG8gd29ybGQ=", "does not contain a JWT")]
    public void Rejects_non_jwt_content(string content, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SpiffeAssertionSource.Normalize(content, "/p"));
        Assert.Contains(message, ex.Message);
        Assert.Contains("/p", ex.Message);
    }
}
