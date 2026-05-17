using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerFaultSuppressionTests
{
    [Fact]
    public async Task SuppressUnobservedFault_FaultedTask_DoesNotThrowAndPreservesFaultedState()
    {
        // Authoritatively verifying "the GC finaliser does not raise UnobservedTaskException"
        // would require forcing GC pressure and subscribing to the static event — brittle in
        // test runners. This test verifies the weaker but still useful property: the helper
        // is safe to call on a faulted task and leaves it in IsFaulted state.
        var tcs = new TaskCompletionSource<int>();
        tcs.SetException(new InvalidOperationException("synthetic fault"));

        RequestReplyManager.SuppressUnobservedFault(tcs.Task);

        // The continuation is ExecuteSynchronously on the antecedent completion, so it should
        // already have run by the time SetException returns; a tiny yield is belt-and-braces.
        await Task.Yield();

        Assert.True(tcs.Task.IsFaulted);
        Assert.NotNull(tcs.Task.Exception);
    }

    [Fact]
    public void SuppressUnobservedFault_SuccessfullyCompletedTask_DoesNotThrow()
    {
        // The continuation is OnlyOnFaulted, so a successfully-completed task never invokes it.
        // This test is a smoke test that the helper is safe to call on the success path.
        var completed = Task.FromResult(42);
        RequestReplyManager.SuppressUnobservedFault(completed);
        Assert.Equal(TaskStatus.RanToCompletion, completed.Status);
    }
}
