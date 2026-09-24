using AzureDash.Components.Partials;
using AzureDash.Identity;
using AzureDash.Imds;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/identity", async (IdentityInfoService s, CancellationToken ct) => Results.Json(await s.GetAsync(ct)));
        app.MapGet("/partials/identity", async (IdentityInfoService s, CancellationToken ct) =>
            new RazorComponentResult<IdentityPartial>(new { Info = await s.GetAsync(ct) }));
        app.MapGet("/api/imds", async (ImdsClient c, CancellationToken ct) => Results.Json(await c.GetAsync(ct)));
        app.MapGet("/partials/imds", async (ImdsClient c, CancellationToken ct) =>
            new RazorComponentResult<ImdsPartial>(new { Info = await c.GetAsync(ct) }));
        return app;
    }
}
