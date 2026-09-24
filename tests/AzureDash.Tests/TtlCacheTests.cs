using AzureDash.Inventory;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class TtlCacheTests
{
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    int _calls;

    TtlCache Cache() => new(TimeSpan.FromSeconds(60), _time);

    Func<CancellationToken, Task<object>> Returns(object value) => _ => { _calls++; return Task.FromResult(value); };
    Func<CancellationToken, Task<object>> Throws(Exception ex) => _ => { _calls++; return Task.FromException<object>(ex); };

    [Fact]
    public async Task Caches_within_ttl()
    {
        var c = Cache();
        var first = await c.GetAsync("k", Returns("a"));
        var second = await c.GetAsync("k", Returns("b"));
        Assert.Equal("a", first.Value);
        Assert.Equal("a", second.Value);
        Assert.Equal(_time.Now, second.FetchedAt);
        Assert.Null(second.Error);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Reloads_after_ttl_and_on_force()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("a"));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("b", (await c.GetAsync("k", Returns("b"))).Value);
        Assert.Equal("c", (await c.GetAsync("k", Returns("c"), force: true)).Value);
        Assert.Equal(3, _calls);
    }

    [Fact]
    public async Task Error_keeps_previous_value()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("good"));
        _time.Advance(TimeSpan.FromSeconds(5));
        var entry = await c.GetAsync("k", Throws(new AzureError("denied")), force: true);
        Assert.Equal("good", entry.Value);
        Assert.Equal("denied", entry.Error);
        Assert.Equal(_time.Now, entry.FetchedAt);
    }

    [Fact]
    public async Task First_error_has_no_value_and_names_exception_type()
    {
        var entry = await Cache().GetAsync("k", Throws(new InvalidOperationException("boom")));
        Assert.Null(entry.Value);
        Assert.Equal("InvalidOperationException: boom", entry.Error);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var c = Cache();
        await c.GetAsync("a", Throws(new AzureError("a failed")));
        var b = await c.GetAsync("b", Returns("fine"));
        Assert.Equal("fine", b.Value);
        Assert.Null(b.Error);
    }

    [Fact]
    public async Task Invalidate_all_forces_reload()
    {
        var c = Cache();
        await c.GetAsync("k", Returns("a"));
        c.InvalidateAll();
        Assert.Equal("b", (await c.GetAsync("k", Returns("b"))).Value);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_load()
    {
        var c = Cache();
        var gate = new TaskCompletionSource<object>();
        Func<CancellationToken, Task<object>> slow = _ => { Interlocked.Increment(ref _calls); return gate.Task; };
        var t1 = c.GetAsync("k", slow);
        var t2 = c.GetAsync("k", slow);
        gate.SetResult("v");
        Assert.Equal("v", (await t1).Value);
        Assert.Equal("v", (await t2).Value);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Load_in_flight_during_invalidate_is_not_stored()
    {
        var c = Cache();
        var gate = new TaskCompletionSource<object>();
        var inflight = c.GetAsync("k", _ => gate.Task);
        c.InvalidateAll();
        gate.SetResult("stale");
        Assert.Equal("stale", (await inflight).Value);
        Assert.Equal("fresh", (await c.GetAsync("k", Returns("fresh"))).Value);
    }
}
