using AzureDash.Load;

namespace AzureDash.Tests;

public class CpuLoadTests
{
    [Fact]
    public void Starts_and_stops_workers()
    {
        using var cpu = new CpuLoad(maxWorkers: 4);
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Status);
        Assert.Equal(new CpuLoadStatus(true, 2), cpu.Start(2));
        Assert.Equal(new CpuLoadStatus(true, 2), cpu.Status);
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Stop());
        Assert.Equal(new CpuLoadStatus(false, 0), cpu.Status);
    }

    [Fact]
    public void Clamps_workers_to_the_maximum()
    {
        using var cpu = new CpuLoad(maxWorkers: 2);
        Assert.Equal(2, cpu.Start(64).Workers);
    }

    [Fact]
    public void Start_is_idempotent_while_running()
    {
        using var cpu = new CpuLoad(maxWorkers: 4);
        cpu.Start(1);
        Assert.Equal(1, cpu.Start(3).Workers);
    }

    [Fact]
    public void Stop_when_idle_is_a_no_op()
    {
        using var cpu = new CpuLoad(maxWorkers: 1);
        Assert.False(cpu.Stop().Active);
    }
}
