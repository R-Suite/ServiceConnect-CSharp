using System.Collections.Concurrent;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Snapshot of one stream reassembled by the receiver. <see cref="FlowId"/>
/// matches the driver's per-flow correlator (carried on the message body's
/// correlation id because the stream API has no caller-header pathway);
/// <see cref="BusTag"/> identifies the bus the handler ran on; <see cref="Bytes"/>
/// is the count of reassembled bytes; <see cref="Sha256"/> is the SHA-256 hex
/// digest of those bytes, which the driver compares against the sent-side digest.
/// </summary>
public sealed record StreamObservation(Guid FlowId, string BusTag, int Bytes, string Sha256);

/// <summary>
/// Process-wide observation log for the streaming driver. Each
/// <see cref="ServiceConnect.Interfaces.IStreamHandler{TMessage}.ExecuteAsync"/>
/// invocation records one observation keyed by flow id; the driver awaits the
/// per-flow rendezvous and asserts the recorded SHA matches the sent-side digest.
/// </summary>
/// <remarks>
/// The TaskCompletionSource is one-shot per flow id — a stream is reassembled
/// once and dispatched once, so a second completion (e.g. a broker redelivery
/// after dispatch) would race the first observation's recorded bytes. The
/// framework's stream eviction sweep and the dispatch-in-flight CAS together
/// make the second-dispatch case rare; if it ever fires, the assertion runs
/// against the first observation, which is the right invariant for the harness's
/// integrity check.
/// </remarks>
public sealed class StreamObservations
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StreamObservation>> _waiters = new();

    /// <summary>
    /// Returns a task that completes when the first stream observation for
    /// <paramref name="flowId"/> is recorded. Safe to call before or after the
    /// framework dispatches the reassembled stream; first caller wins the TCS
    /// allocation.
    /// </summary>
    public Task<StreamObservation> AwaitAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var tcs = _waiters.GetOrAdd(flowId, _ => new TaskCompletionSource<StreamObservation>(TaskCreationOptions.RunContinuationsAsynchronously));
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    /// <summary>
    /// Records a reassembled stream and signals any pending awaiter on that flow
    /// id. Called from
    /// <see cref="DocumentUploadedHandler.ExecuteAsync"/> after the framework
    /// completes the stream reassembly and dispatch.
    /// </summary>
    public void Record(StreamObservation observation)
    {
        var tcs = _waiters.GetOrAdd(observation.FlowId, _ => new TaskCompletionSource<StreamObservation>(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(observation);
    }
}
