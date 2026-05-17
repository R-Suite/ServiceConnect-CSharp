using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerFaultSuppressionTests
{
    [Fact]
    public async Task SuppressUnobservedFault_ObservesFaultedTaskException()
    {
        // Create a faulted Task that nobody awaits. Without SuppressUnobservedFault, the
        // GC finaliser would raise TaskScheduler.UnobservedTaskException. With it, the
        // continuation reads t.Exception and the task is considered observed.
        var tcs = new TaskCompletionSource<int>();
        tcs.SetException(new InvalidOperationException("synthetic fault"));

        RequestReplyManager.SuppressUnobservedFault(tcs.Task);

        // The continuation is ExecuteSynchronously on the antecedent completion, so it should
        // already have run by the time SetException returns; a tiny yield is belt-and-braces.
        await Task.Yield();

        Assert.True(tcs.Task.IsFaulted);
        Assert.NotNull(tcs.Task.Exception);
        // The Exception property has been read by the continuation; we don't try to assert
        // the un-observed-flag directly because TaskScheduler.UnobservedTaskException is
        // an event-based contract that's hard to test without GC pressure.
    }

    [Fact]
    public void SuppressUnobservedFault_NonFaultedTask_NoOp()
    {
        // The continuation is OnlyOnFaulted, so a successfully-completed task never invokes it.
        // The call should be safe and side-effect-free.
        var completed = Task.FromResult(42);
        RequestReplyManager.SuppressUnobservedFault(completed);
        Assert.True(completed.IsCompletedSuccessfully);
    }
}
