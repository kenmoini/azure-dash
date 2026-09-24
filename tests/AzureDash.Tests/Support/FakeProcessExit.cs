using AzureDash.State;

namespace AzureDash.Tests.Support;

public sealed class FakeProcessExit : IProcessExit
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<int> Exited => _exited.Task;
    public void Exit(int code) => _exited.TrySetResult(code);
}
