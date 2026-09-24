using System.Text.Json;

namespace AzureDash.Tests.Support;

public static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, params (string Key, string Value)[] fields) =>
        client.SendAsync(Form(url, htmx: false, fields));

    public static Task<HttpResponseMessage> HxPostFormAsync(this HttpClient client, string url, params (string Key, string Value)[] fields) =>
        client.SendAsync(Form(url, htmx: true, fields));

    public static Task<HttpResponseMessage> HxGetAsync(this HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return client.SendAsync(request);
    }

    static HttpRequestMessage Form(string url, bool htmx, (string Key, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        if (htmx) request.Headers.Add("HX-Request", "true");
        return request;
    }
}
