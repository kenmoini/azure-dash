using System.Text;

namespace AzureDash.Identity;

/// <summary>
/// Supplies the SPIFFE JWT-SVID written by spiffe-helper as the client assertion. Re-reads the file on every
/// token exchange because ZTWIM JWT-SVIDs can live only ~5 minutes.
/// </summary>
public sealed class SpiffeAssertionSource(string path)
{
    public async Task<string> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"SPIFFE JWT-SVID file not found at {path} (is the spiffe-helper sidecar running?)", path);
        return Normalize(await File.ReadAllTextAsync(path, ct), path);
    }

    public static string Normalize(string raw, string path)
    {
        var text = raw.Trim();
        if (text.Length == 0) throw new InvalidOperationException($"SPIFFE JWT-SVID file {path} is empty");
        if (LooksLikeJwt(text)) return text;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            if (LooksLikeJwt(decoded)) return decoded;
        }
        catch (FormatException)
        {
        }
        throw new InvalidOperationException($"SPIFFE JWT-SVID file {path} does not contain a JWT");
    }

    static bool LooksLikeJwt(string s) => s.StartsWith("eyJ", StringComparison.Ordinal) && s.Count(c => c == '.') == 2;
}
