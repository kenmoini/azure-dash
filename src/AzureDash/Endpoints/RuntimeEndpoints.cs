using AzureDash.Runtime;

namespace AzureDash.Endpoints;

public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime", (RuntimeInfoProvider p) => Results.Json(p.Get()));
        return app;
    }
}
