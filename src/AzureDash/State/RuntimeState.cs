namespace AzureDash.State;

/// <summary>Probe flags flipped by the controls; in-memory only, so a restart resets them to healthy.</summary>
public sealed class RuntimeState(TimeProvider time)
{
    private readonly object _gate = new();
    private bool _live = true;
    private bool _ready = true;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public bool Live
    {
        get { lock (_gate) return _live; }
        set { lock (_gate) _live = value; }
    }

    public bool Ready
    {
        get { lock (_gate) return _ready; }
        set { lock (_gate) _ready = value; }
    }
}
