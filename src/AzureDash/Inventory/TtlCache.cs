using System.Collections.Concurrent;

namespace AzureDash.Inventory;

public sealed record CacheEntry(object? Value, DateTimeOffset? FetchedAt, string? Error);

/// <summary>Per-key TTL cache: one load at a time per key; failures keep the last good value.</summary>
public sealed class TtlCache(TimeSpan ttl, TimeProvider time)
{
    private sealed record Slot(CacheEntry Entry, DateTimeOffset FreshUntil, long Epoch);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private long _epoch;

    public async Task<CacheEntry> GetAsync(
        string key, Func<CancellationToken, Task<object>> loader, bool force = false, CancellationToken ct = default)
    {
        if (!force && TryFresh(key, out var fresh)) return fresh;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!force && TryFresh(key, out fresh)) return fresh;

            var epoch = Interlocked.Read(ref _epoch);
            var previous = _slots.TryGetValue(key, out var old) ? old.Entry.Value : null;
            CacheEntry entry;
            try
            {
                entry = new CacheEntry(await loader(ct), time.GetUtcNow(), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var message = ex is AzureError ? ex.Message : $"{ex.GetType().Name}: {AzureError.FirstLine(ex.Message)}";
                entry = new CacheEntry(previous, time.GetUtcNow(), message);
            }

            if (Interlocked.Read(ref _epoch) == epoch)
                _slots[key] = new Slot(entry, time.GetUtcNow() + ttl, epoch);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _epoch);
        _slots.Clear();
    }

    private bool TryFresh(string key, out CacheEntry entry)
    {
        if (_slots.TryGetValue(key, out var slot) && slot.Epoch == Interlocked.Read(ref _epoch) && time.GetUtcNow() < slot.FreshUntil)
        {
            entry = slot.Entry;
            return true;
        }
        entry = null!;
        return false;
    }
}
