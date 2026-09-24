namespace AzureDash.Load;

public sealed record CpuLoadStatus(bool Active, int Workers);

/// <summary>Burns CPU on N background threads (no GIL in .NET, so threads saturate cores).</summary>
public sealed class CpuLoad : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maxWorkers;
    private readonly List<Thread> _threads = [];
    private CancellationTokenSource? _cts;

    public CpuLoad(int? maxWorkers = null) => _maxWorkers = Math.Max(1, maxWorkers ?? Environment.ProcessorCount);

    public CpuLoadStatus Status
    {
        get { lock (_gate) return new(_cts is not null, _threads.Count); }
    }

    public CpuLoadStatus Start(int workers)
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var count = Math.Clamp(workers, 1, _maxWorkers);
                for (var i = 0; i < count; i++)
                {
                    var thread = new Thread(() => Spin(token)) { IsBackground = true, Name = $"cpu-load-{i}" };
                    thread.Start();
                    _threads.Add(thread);
                }
            }
            return new(true, _threads.Count);
        }
    }

    public CpuLoadStatus Stop()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                _cts.Cancel();
                foreach (var thread in _threads) thread.Join(TimeSpan.FromSeconds(2));
                _threads.Clear();
                _cts.Dispose();
                _cts = null;
            }
            return new(false, 0);
        }
    }

    public void Dispose() => Stop();

    private static void Spin(CancellationToken token)
    {
        while (!token.IsCancellationRequested) Thread.SpinWait(10_000);
    }
}
