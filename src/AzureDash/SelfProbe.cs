using AzureDash.Configuration;

namespace AzureDash;

/// <summary>`dotnet AzureDash.dll --healthcheck`: container HEALTHCHECK without curl in the image.</summary>
public static class SelfProbe
{
    public static Uri LocalUrl(EnvLookup env)
    {
        var ports = AppSettings.Blank(env("ASPNETCORE_HTTP_PORTS")) ?? "8080";
        var port = ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return new Uri($"http://127.0.0.1:{port}/healthz/live");
    }

    public static async Task<int> RunAsync(Uri url, HttpMessageHandler handler, TextWriter output)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(url);
            await output.WriteLineAsync($"{(int)response.StatusCode} {url}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync($"unhealthy: {ex.Message}");
            return 1;
        }
    }
}
