using AzureDash.Components.Partials;
using AzureDash.Configuration;
using AzureDash.Load;
using AzureDash.State;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureDash.Endpoints;

public sealed record CrashResponse(string Message, int ExitCode);

public static class ControlsEndpoints
{
    public static IEndpointRouteBuilder MapControlsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/state", (StateService s) => Results.Json(s.Snapshot()));

        var controls = app.MapGroup("/controls").AddEndpointFilter(RequireControlsEnabled);
        controls.MapPost("/liveness", (HttpContext ctx, RuntimeState st, StateService s) => Toggle(ctx, s, v => st.Live = v));
        controls.MapPost("/readiness", (HttpContext ctx, RuntimeState st, StateService s) => Toggle(ctx, s, v => st.Ready = v));
        controls.MapPost("/cpu", Cpu);
        controls.MapPost("/crash", Crash);
        return app;
    }

    static async Task<IResult> Toggle(HttpContext ctx, StateService s, Action<bool> apply)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Bool(form, "enabled") is not bool enabled) return Unprocessable("'enabled' must be true or false");
        apply(enabled);
        return Respond(ctx, s);
    }

    static async Task<IResult> Cpu(HttpContext ctx, CpuLoad cpu, StateService s)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Bool(form, "enabled") is not bool enabled) return Unprocessable("'enabled' must be true or false");
        if (Input.Int(form, "workers", 1, 1, 64) is not int workers) return Unprocessable("'workers' must be an integer between 1 and 64");
        if (enabled) cpu.Start(workers); else cpu.Stop();
        return Respond(ctx, s);
    }

    static async Task<IResult> Crash(HttpContext ctx, CpuLoad cpu, IProcessExit exit)
    {
        var form = await Input.ReadAsync(ctx.Request);
        if (Input.Int(form, "exit_code", 1, 0, 255) is not int code) return Unprocessable("'exit_code' must be an integer between 0 and 255");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            cpu.Stop();
            exit.Exit(code);
        });
        return Htmx.IsHtmx(ctx.Request)
            ? Results.Content($"<span class=\"badge bad\">Exiting with code {code}…</span>", "text/html", statusCode: 202)
            : Results.Json(new CrashResponse($"exiting with code {code}", code), statusCode: 202);
    }

    static IResult Respond(HttpContext ctx, StateService s)
    {
        var snapshot = s.Snapshot();
        return Htmx.IsHtmx(ctx.Request)
            ? new RazorComponentResult<ControlsPartial>(new { Snapshot = snapshot })
            : Results.Json(snapshot);
    }

    static IResult Unprocessable(string detail) => Results.Json(new ErrorBody(detail), statusCode: 422);

    static async ValueTask<object?> RequireControlsEnabled(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (ctx.HttpContext.RequestServices.GetRequiredService<AppSettings>().ControlsEnabled) return await next(ctx);
        return Results.Json(new ErrorBody("controls are disabled (CONTROLS_ENABLED=false)"), statusCode: 403);
    }
}
