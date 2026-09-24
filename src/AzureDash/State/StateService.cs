using AzureDash.Configuration;
using AzureDash.Load;

namespace AzureDash.State;

public sealed record StateSnapshot(
    bool Live, bool Ready, DateTimeOffset StartedAt, double UptimeSeconds, bool ControlsEnabled, CpuLoadStatus CpuLoad);

public sealed class StateService(RuntimeState state, CpuLoad cpu, AppSettings settings, TimeProvider time)
{
    public StateSnapshot Snapshot() => new(
        state.Live,
        state.Ready,
        state.StartedAt,
        Math.Round((time.GetUtcNow() - state.StartedAt).TotalSeconds, 1),
        settings.ControlsEnabled,
        cpu.Status);
}
