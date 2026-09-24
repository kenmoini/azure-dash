namespace AzureDash.State;

public interface IProcessExit
{
    void Exit(int code);
}

public sealed class EnvironmentProcessExit : IProcessExit
{
    public void Exit(int code) => Environment.Exit(code);
}
