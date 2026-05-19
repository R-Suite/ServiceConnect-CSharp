using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Per-flow rendezvous between a pattern driver (the awaiter) and the message handler
/// running on the receiving bus (the signaller). A single shared instance is registered
/// on both buses so a driver thread can await the in-process arrival of its handler
/// without polling the broker or counting messages.
/// </summary>
/// <remarks>
/// <para>
/// The handler may signal before the driver registers its waiter (broker dispatches
/// faster than the awaiting task is scheduled) — and vice versa. <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// resolves the race: whichever side gets there first allocates the <see cref="TaskCompletionSource{TResult}"/>;
/// the second side reuses the same instance. <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
/// keeps the signalling handler thread off the driver's continuation, so a slow assertion
/// path on the driver side never holds the consumer pump.
/// </para>
/// <para>
/// Headers are snapshot eagerly inside <see cref="Signal"/> rather than held as a live
/// <see cref="IConsumeContext"/> reference. The framework pools consume contexts and
/// invalidates the rental token the moment the handler returns; with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> the driver's
/// continuation routinely lands after the handler thread has unwound and the context has
/// been released, at which point any header access throws. Copying the dictionary here
/// once is a few microseconds and decouples the driver entirely from pool lifetime.
/// </para>
/// <para>
/// Entries are never reclaimed for the lifetime of the harness process — flow ids are
/// cryptographic GUIDs, so memory growth is bounded by the test run's total flow count.
/// A soak run that wants to drop completed entries should swap this for a sliding-window
/// implementation, but the smoke and throughput modes don't need it.
/// </para>
/// </remarks>
public sealed class PerHandlerSignal
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<HandlerInvocation>> _waiters = new();

    /// <summary>
    /// Returns a task that completes when the handler for <paramref name="flowId"/> fires.
    /// Safe to call before or after the handler runs — first caller wins the allocation.
    /// </summary>
    /// <param name="flowId">Per-flow correlator stamped on the outbound headers.</param>
    /// <param name="cancellationToken">Cancels the wait independently of broker delivery.</param>
    public Task<HandlerInvocation> AwaitAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var tcs = _waiters.GetOrAdd(flowId, _ => new TaskCompletionSource<HandlerInvocation>(TaskCreationOptions.RunContinuationsAsynchronously));
        // Register fires once; CTS disposal at flow scope drops the registration. The
        // closure captures the same TCS the GetOrAdd produced so a late signal arrives
        // at a cancelled TCS (TrySetResult returns false) without faulting the awaiter.
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    /// <summary>
    /// Records that the handler for <paramref name="flowId"/> ran on the bus identified by
    /// <paramref name="busTag"/>, completing any pending await with a snapshot of the
    /// inbound headers the handler saw. The snapshot is taken eagerly here so the driver's
    /// continuation can inspect headers after the framework has released the underlying
    /// pooled <see cref="IConsumeContext"/>.
    /// </summary>
    public void Signal(Guid flowId, string busTag, IConsumeContext context)
    {
        // Shallow copy of the header dictionary while the context is still active. The
        // values are either string or byte[] — both reference-immutable — so passing the
        // copy across the rendezvous is safe even after the context is released.
        var snapshot = new Dictionary<string, object>(context.Headers, StringComparer.Ordinal);
        var tcs = _waiters.GetOrAdd(flowId, _ => new TaskCompletionSource<HandlerInvocation>(TaskCreationOptions.RunContinuationsAsynchronously));
        // TrySetResult: a redelivered message (at-least-once delivery) would re-enter the
        // handler and call Signal again; the second invocation is a no-op rather than an
        // InvalidOperationException because the task is already completed.
        tcs.TrySetResult(new HandlerInvocation(busTag, snapshot));
    }
}

/// <summary>
/// The single observation a driver collects per flow: which bus the handler ran on
/// (<see cref="BusTag"/>) and the header snapshot copied from the consume context at
/// signal time, so the driver can run cross-tenant header assertions independently of
/// the framework's per-message context pool lifetime.
/// </summary>
public sealed record HandlerInvocation(string BusTag, IReadOnlyDictionary<string, object> Headers);
