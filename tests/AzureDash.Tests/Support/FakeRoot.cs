namespace AzureDash.Tests.Support;

/// <summary>A temporary directory standing in for "/" so /proc, /sys and /etc can be faked.</summary>
public sealed class FakeRoot : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("azure-dash-root-").FullName;

    public FakeRoot Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public FakeRoot Dir(string relative)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));
        return this;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
