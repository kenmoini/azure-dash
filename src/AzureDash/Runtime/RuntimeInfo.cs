using AzureDash.State;

namespace AzureDash.Runtime;

public sealed record PodInfo(string? Name, string? Namespace, string? Node, string? Ip, string? ServiceAccount);

public sealed record CgroupInfo(string Version, double? CpuLimitCores, long? MemoryLimitBytes, long? MemoryUsageBytes);

public sealed record RuntimeInfo(
    string Hostname,
    string ContainerRuntime,
    string Orchestrator,
    PodInfo? Pod,
    CgroupInfo Cgroup,
    string Os,
    string Dotnet,
    string Gc,
    int ProcessorCount,
    int? Uid,
    int? Gid,
    int Pid,
    double? LoadAverage1m,
    IReadOnlyDictionary<string, string?> EnvHints,
    StateSnapshot State);
