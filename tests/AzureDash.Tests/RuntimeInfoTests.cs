using AzureDash.Configuration;
using AzureDash.Load;
using AzureDash.Runtime;
using AzureDash.State;
using AzureDash.Tests.Support;

namespace AzureDash.Tests;

public class RuntimeInfoTests
{
    static RuntimeInfo Get(FakeRoot root, params (string Key, string Value)[] env)
    {
        var d = env.ToDictionary(e => e.Key, e => e.Value);
        var state = new StateService(new RuntimeState(TimeProvider.System), new CpuLoad(1), new AppSettings(), TimeProvider.System);
        return new RuntimeInfoProvider(root.Path, name => d.GetValueOrDefault(name), state).Get();
    }

    [Fact]
    public void Detects_podman() { using var r = new FakeRoot().Write("run/.containerenv", ""); Assert.Equal("podman", Get(r).ContainerRuntime); }

    [Fact]
    public void Detects_docker() { using var r = new FakeRoot().Write(".dockerenv", ""); Assert.Equal("docker", Get(r).ContainerRuntime); }

    [Fact]
    public void Uses_container_env_var() { using var r = new FakeRoot(); Assert.Equal("oci", Get(r, ("container", "oci")).ContainerRuntime); }

    [Theory]
    [InlineData("0::/kubepods.slice/crio-abc.scope", "cri-o")]
    [InlineData("0::/kubepods/besteffort/containerd-abc", "containerd")]
    [InlineData("0::/kubepods/pod123", "kubernetes")]
    [InlineData("0::/", "none")]
    public void Detects_runtime_from_cgroup_path(string cgroup, string expected)
    {
        using var r = new FakeRoot().Write("proc/self/cgroup", cgroup);
        Assert.Equal(expected, Get(r).ContainerRuntime);
    }

    [Fact]
    public void Kubernetes_pod_fields_from_downward_api()
    {
        using var r = new FakeRoot();
        var info = Get(r, ("KUBERNETES_SERVICE_HOST", "10.0.0.1"), ("POD_NAME", "azure-dash-abc"), ("POD_NAMESPACE", "azure-dash"),
            ("NODE_NAME", "aks-nodepool1-0"), ("POD_IP", "10.244.0.5"), ("SERVICE_ACCOUNT", "azure-dash"));
        Assert.Equal("kubernetes", info.Orchestrator);
        Assert.Equal(new PodInfo("azure-dash-abc", "azure-dash", "aks-nodepool1-0", "10.244.0.5", "azure-dash"), info.Pod);
        Assert.Equal("10.0.0.1", info.EnvHints["KUBERNETES_SERVICE_HOST"]);
    }

    [Fact]
    public void Namespace_falls_back_to_service_account_file()
    {
        using var r = new FakeRoot().Write("var/run/secrets/kubernetes.io/serviceaccount/namespace", "from-file\n");
        var info = Get(r);
        Assert.Equal("kubernetes", info.Orchestrator);
        Assert.Equal("from-file", info.Pod!.Namespace);
    }

    [Fact]
    public void No_orchestrator_means_no_pod()
    {
        using var r = new FakeRoot();
        var info = Get(r);
        Assert.Equal("none", info.Orchestrator);
        Assert.Null(info.Pod);
    }

    [Fact]
    public void Cgroup_v2_limits()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cgroup.controllers", "cpu memory")
            .Write("sys/fs/cgroup/cpu.max", "50000 100000\n").Write("sys/fs/cgroup/memory.max", "536870912\n")
            .Write("sys/fs/cgroup/memory.current", "1048576\n");
        Assert.Equal(new CgroupInfo("v2", 0.5, 536870912, 1048576), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v2_unlimited()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cgroup.controllers", "")
            .Write("sys/fs/cgroup/cpu.max", "max 100000").Write("sys/fs/cgroup/memory.max", "max");
        Assert.Equal(new CgroupInfo("v2", null, null, null), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v1_limits_and_huge_limit_means_unlimited()
    {
        using var r = new FakeRoot()
            .Write("sys/fs/cgroup/cpu/cpu.cfs_quota_us", "200000").Write("sys/fs/cgroup/cpu/cpu.cfs_period_us", "100000")
            .Write("sys/fs/cgroup/memory/memory.limit_in_bytes", "9223372036854771712")
            .Write("sys/fs/cgroup/memory/memory.usage_in_bytes", "4096");
        Assert.Equal(new CgroupInfo("v1", 2, null, 4096), Get(r).Cgroup);
    }

    [Fact]
    public void Cgroup_v1_negative_quota_is_unlimited()
    {
        using var r = new FakeRoot().Write("sys/fs/cgroup/cpu/cpu.cfs_quota_us", "-1").Write("sys/fs/cgroup/cpu/cpu.cfs_period_us", "100000");
        Assert.Null(Get(r).Cgroup.CpuLimitCores);
    }

    [Fact]
    public void No_cgroup_fs()
    {
        using var r = new FakeRoot();
        Assert.Equal(new CgroupInfo("none", null, null, null), Get(r).Cgroup);
    }

    [Fact]
    public void Os_uid_gid_and_load_average()
    {
        using var r = new FakeRoot()
            .Write("etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\n")
            .Write("proc/self/status", "Name:\tdotnet\nUid:\t1654\t1654\t1654\t1654\nGid:\t1654\t1654\t1654\t1654\n")
            .Write("proc/loadavg", "0.42 0.30 0.20 1/100 12345\n");
        var info = Get(r);
        Assert.Equal("Ubuntu 24.04.3 LTS", info.Os);
        Assert.Equal(1654, info.Uid);
        Assert.Equal(1654, info.Gid);
        Assert.Equal(0.42, info.LoadAverage1m);
        Assert.StartsWith(".NET", info.Dotnet);
        Assert.Equal(Environment.ProcessId, info.Pid);
    }

    [Fact]
    public async Task Api_runtime_includes_state()
    {
        using var f = new AppFactory();
        var json = await (await f.CreateClient().GetAsync("/api/runtime")).JsonAsync();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("hostname").GetString()));
        Assert.True(json.GetProperty("state").GetProperty("live").GetBoolean());
        Assert.True(json.TryGetProperty("cgroup", out _));
    }
}
