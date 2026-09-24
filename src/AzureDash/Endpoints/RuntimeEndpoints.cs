using AzureDash.Components.Partials;
using AzureDash.Runtime;
using AzureDash.State;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime", (RuntimeInfoProvider p) => Results.Json(p.Get()));
        app.MapGet("/partials/runtime", (RuntimeInfoProvider p) => new RazorComponentResult<RuntimePartial>(new { Info = p.Get() }));
        app.MapGet("/partials/controls", (StateService s) => new RazorComponentResult<ControlsPartial>(new { Snapshot = s.Snapshot() }));
        return app;
    }
}
