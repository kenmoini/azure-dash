using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using AzureDash.Configuration;
using AzureDash.State;

namespace AzureDash.Runtime;

/// <summary>Container/Kubernetes/cgroup introspection. <paramref name="root"/> is "/" in production, a temp dir in tests.</summary>
public sealed class RuntimeInfoProvider(string root, EnvLookup env, StateService state)
{
    private const long Unlimited = 1L << 62;

    public RuntimeInfo Get()
    {
        var orchestrator = DetectOrchestrator();
        return new RuntimeInfo(
            Hostname: Environment.MachineName,
            ContainerRuntime: DetectContainerRuntime(),
            Orchestrator: orchestrator,
            Pod: orchestrator == "kubernetes" ? ReadPod() : null,
            Cgroup: ReadCgroup(),
            Os: ReadOs(),
            Dotnet: RuntimeInformation.FrameworkDescription,
            Gc: GCSettings.IsServerGC ? "server" : "workstation",
            ProcessorCount: Environment.ProcessorCount,
            Uid: ReadStatusId("Uid:"),
            Gid: ReadStatusId("Gid:"),
            Pid: Environment.ProcessId,
            LoadAverage1m: ReadLoadAverage(),
            EnvHints: new Dictionary<string, string?>
            {
                ["KUBERNETES_SERVICE_HOST"] = env("KUBERNETES_SERVICE_HOST"),
                ["container"] = env("container"),
                ["HOSTNAME"] = env("HOSTNAME"),
            },
            State: state.Snapshot());
    }

    private string P(string relative) => Path.Combine(root, relative);

    private string? ReadText(string relative)
    {
        try
        {
            var path = P(relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DetectContainerRuntime()
    {
        if (File.Exists(P("run/.containerenv"))) return "podman";
        if (File.Exists(P(".dockerenv"))) return "docker";
        if (AppSettings.Blank(env("container")) is { } fromEnv) return fromEnv;
        var cgroup = ReadText("proc/self/cgroup") ?? "";
        if (cgroup.Contains("crio")) return "cri-o";
        if (cgroup.Contains("containerd")) return "containerd";
        if (cgroup.Contains("docker")) return "docker";
        if (cgroup.Contains("kubepods")) return "kubernetes";
        return "none";
    }

    private string DetectOrchestrator() =>
        AppSettings.Blank(env("KUBERNETES_SERVICE_HOST")) is not null
        || File.Exists(P("var/run/secrets/kubernetes.io/serviceaccount/namespace"))
            ? "kubernetes"
            : "none";

    private PodInfo ReadPod() => new(
        AppSettings.Blank(env("POD_NAME")),
        AppSettings.Blank(env("POD_NAMESPACE")) ?? AppSettings.Blank(ReadText("var/run/secrets/kubernetes.io/serviceaccount/namespace")),
        AppSettings.Blank(env("NODE_NAME")),
        AppSettings.Blank(env("POD_IP")),
        AppSettings.Blank(env("SERVICE_ACCOUNT")));

    private CgroupInfo ReadCgroup()
    {
        if (File.Exists(P("sys/fs/cgroup/cgroup.controllers")))
        {
            double? cpu = null;
            var parts = ReadText("sys/fs/cgroup/cpu.max")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is [var q, var p] && long.TryParse(q, out var quota) && long.TryParse(p, out var period) && quota > 0 && period > 0)
                cpu = Math.Round((double)quota / period, 2);
            return new CgroupInfo("v2", cpu, Limit(ReadText("sys/fs/cgroup/memory.max")), Limit(ReadText("sys/fs/cgroup/memory.current")));
        }
        if (Directory.Exists(P("sys/fs/cgroup/cpu")) || Directory.Exists(P("sys/fs/cgroup/memory")))
        {
            double? cpu = null;
            if (Limit(ReadText("sys/fs/cgroup/cpu/cpu.cfs_quota_us")) is long quota and > 0
                && Limit(ReadText("sys/fs/cgroup/cpu/cpu.cfs_period_us")) is long period and > 0)
                cpu = Math.Round((double)quota / period, 2);
            return new CgroupInfo("v1", cpu,
                Limit(ReadText("sys/fs/cgroup/memory/memory.limit_in_bytes")),
                Limit(ReadText("sys/fs/cgroup/memory/memory.usage_in_bytes")));
        }
        return new CgroupInfo("none", null, null, null);
    }

    /// <summary>"max", negative, unparsable or ≥ 2^62 all mean "no limit".</summary>
    internal static long? Limit(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < Unlimited ? v : null;

    private string ReadOs()
    {
        foreach (var line in (ReadText("etc/os-release") ?? "").Split('\n'))
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                return line["PRETTY_NAME=".Length..].Trim().Trim('"');
        return RuntimeInformation.OSDescription;
    }

    private int? ReadStatusId(string prefix)
    {
        foreach (var line in (ReadText("proc/self/status") ?? "").Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var fields = line[prefix.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0 && int.TryParse(fields[0], out var id) ? id : null;
            }
        return null;
    }

    private double? ReadLoadAverage()
    {
        var first = ReadText("proc/loadavg")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
