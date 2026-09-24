using System.Globalization;

namespace AzureDash.Components;

public static class Fmt
{
    static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    public static string Bytes(long? bytes, string nullText = "—")
    {
        if (bytes is not long v) return nullText;
        double d = v;
        var i = 0;
        while (d >= 1024 && i < Units.Length - 1) { d /= 1024; i++; }
        return i == 0 ? $"{v} B" : string.Create(CultureInfo.InvariantCulture, $"{d:0.0} {Units[i]}");
    }

    public static string Cores(double? cores) =>
        cores is double c ? string.Create(CultureInfo.InvariantCulture, $"{c:0.##} cores") : "unlimited";

    public static string Time(DateTimeOffset? t) =>
        t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "never";

    public static string Duration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public static string Until(DateTimeOffset target, DateTimeOffset now)
    {
        var d = target - now;
        return d <= TimeSpan.Zero ? "expired" : $"in {Duration(d.TotalSeconds)}";
    }
}
