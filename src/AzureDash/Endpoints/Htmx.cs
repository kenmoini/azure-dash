namespace AzureDash.Endpoints;

public static class Htmx
{
    public static bool IsHtmx(HttpRequest request) => request.Headers["HX-Request"] == "true";
}
