using System.Text.Json.Serialization;
using AzureDash.State;

namespace AzureDash.Endpoints;

public sealed record ProbeStatus(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz/live", (RuntimeState s) => Probe(s.Live, "liveness"));
        app.MapGet("/healthz/ready", (RuntimeState s) => Probe(s.Ready, "readiness"));
        return app;
    }

    static IResult Probe(bool ok, string what) => ok
        ? Results.Json(new ProbeStatus("ok", null))
        : Results.Json(new ProbeStatus("failing", $"{what} disabled via controls"), statusCode: 503);
}
