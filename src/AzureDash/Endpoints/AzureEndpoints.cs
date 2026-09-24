using AzureDash.Components.Partials;
using AzureDash.Configuration;
using AzureDash.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public sealed record AzureApiResponse(string Kind, DateTimeOffset? FetchedAt, string? Error, object? Items);

public static class AzureEndpoints
{
    public static IEndpointRouteBuilder MapAzureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/azure/{kind}", Api);
        app.MapPost("/api/azure/refresh", (AzureService azure) =>
        {
            azure.InvalidateAll();
            return Results.NoContent();
        });
        app.MapGet("/partials/azure/{kind}", Partial);
        return app;
    }

    static async Task<IResult> Api(string kind, HttpRequest request, AzureService azure, CancellationToken ct)
    {
        if (!AzureKinds.IsKnown(kind)) return Results.Json(new ErrorBody($"unknown kind '{kind}'"), statusCode: 404);
        var entry = await azure.FetchAsync(kind, IsRefresh(request), ct);
        return Results.Json(new AzureApiResponse(kind, entry.FetchedAt, entry.Error, entry.Value));
    }

    static async Task<IResult> Partial(string kind, HttpRequest request, AzureService azure, CancellationToken ct)
    {
        if (!AzureKinds.IsKnown(kind)) return Results.NotFound();
        var entry = await azure.FetchAsync(kind, IsRefresh(request), ct);
        return new RazorComponentResult<AzurePanel>(new { Kind = kind, Entry = entry });
    }

    static bool IsRefresh(HttpRequest request) => AppSettings.TryParseBool(request.Query["refresh"].ToString()) == true;
}
