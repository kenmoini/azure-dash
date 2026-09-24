using AzureDash.Components;

namespace AzureDash.Tests;

public class FmtTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(512L, "512 B")]
    [InlineData(536870912L, "512.0 MiB")]
    [InlineData(1610612736L, "1.5 GiB")]
    public void Bytes(long? value, string expected) => Assert.Equal(expected, Fmt.Bytes(value));

    [Fact]
    public void Bytes_null_text() => Assert.Equal("unlimited", Fmt.Bytes(null, "unlimited"));

    [Fact]
    public void Cores() { Assert.Equal("0.5 cores", Fmt.Cores(0.5)); Assert.Equal("unlimited", Fmt.Cores(null)); }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(125, "2m 5s")]
    [InlineData(7260, "2h 1m")]
    [InlineData(90000, "1d 1h")]
    public void Duration(double seconds, string expected) => Assert.Equal(expected, Fmt.Duration(seconds));

    [Fact]
    public void Time_and_until()
    {
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal("2026-01-02 03:04:05 UTC", Fmt.Time(t));
        Assert.Equal("never", Fmt.Time(null));
        Assert.Equal("in 1m 0s", Fmt.Until(t.AddMinutes(1), t));
        Assert.Equal("expired", Fmt.Until(t, t.AddSeconds(1)));
    }

    [Fact]
    public void Or() { Assert.Equal("—", Fmt.Or(" ")); Assert.Equal("x", Fmt.Or("x")); }
}
