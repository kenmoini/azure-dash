using System.Text.Json;
using AzureDash.Configuration;

namespace AzureDash.Imds;

public sealed record ImdsInfo(bool Available, string? Reason, IReadOnlyDictionary<string, string>? Compute, DateTimeOffset FetchedAt);

/// <summary>
/// Azure Instance Metadata Service. On AKS this describes the NODE (VMSS instance), not the pod. Unreachable IMDS
/// (not on Azure, or AKS --enable-imds-restriction) is a normal, displayable state.
/// </summary>
public sealed class ImdsClient(HttpClient http, AppSettings settings, TimeProvider time)
{
    public const string HttpClientName = "imds";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    public static readonly IReadOnlyList<string> Fields =
        ["name", "location", "zone", "vmSize", "subscriptionId", "resourceGroupName", "vmScaleSetName", "osType", "vmId"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImdsInfo? _cached;

    public async Task<ImdsInfo> GetAsync(CancellationToken ct)
    {
        if (!settings.ImdsEnabled) return new(false, "disabled (IMDS_ENABLED=false)", null, time.GetUtcNow());
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } c && time.GetUtcNow() - c.FetchedAt < settings.CacheTtl) return c;
            return _cached = await FetchAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImdsInfo> FetchAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var url = $"http://169.254.169.254/metadata/instance/compute?api-version={Uri.EscapeDataString(settings.ImdsApiVersion)}&format=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Metadata", "true");
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode) return Unavailable($"IMDS returned {(int)response.StatusCode} {response.ReasonPhrase}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Unavailable("IMDS response is not a JSON object");
            var compute = new Dictionary<string, string>();
            foreach (var field in Fields)
                if (doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    compute[field] = s;
            return new(true, null, compute, time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("IMDS did not answer within 1s: not running on an Azure VM, or pod access is blocked (AKS --enable-imds-restriction)");
        }
        catch (HttpRequestException ex)
        {
            return Unavailable($"IMDS not reachable: {ex.Message}");
        }
        catch (JsonException)
        {
            return Unavailable("IMDS response is not a JSON object");
        }
    }

    private ImdsInfo Unavailable(string reason) => new(false, reason, null, time.GetUtcNow());
}
