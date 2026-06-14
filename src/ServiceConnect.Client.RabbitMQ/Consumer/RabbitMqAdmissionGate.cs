using System.Diagnostics;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the per-host in-flight counter, shutdown gate, and in-flight gauge metric for a
/// single RabbitMQ consumer host. Separated from <see cref="RabbitMqConsumerHost"/> so the
/// host's <c>EventAsync</c> stays focused on dispatch and ack/nack.
/// </summary>
/// <remarks>
/// Shutdown protocol: callers invoke <see cref="BeginShutdown"/> first, then
/// <see cref="DrainAsync"/>. <see cref="TryAdmit"/> rejects new admissions once shutdown
/// begins. The pairing invariant — every successful <see cref="TryAdmit"/> is followed by
/// exactly one <see cref="Release"/> — is the caller's responsibility; the gate's gauge
/// metric balances on that contract (every +1 emit pairs with one -1 emit carrying
/// identical tags).
/// </remarks>
internal sealed class RabbitMqAdmissionGate(string consumerQueueName)
{
    // The lock guards _inFlight, _shutdownStarted, and _drainTcs together. Increments must
    // happen under the same lock that flips _shutdownStarted so a drain that observes
    // _shutdownStarted=true can never race with an admission that has already incremented
    // but not yet emitted. The metric emit itself runs OUTSIDE the lock to keep the
    // critical section short and to avoid reentrancy via MeterListener callbacks.
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly string _consumerQueueName = consumerQueueName ?? throw new ArgumentNullException(nameof(consumerQueueName));
    private int _inFlight;
    private bool _shutdownStarted;
    private TaskCompletionSource? _drainTcs;

    /// <summary>True once <see cref="BeginShutdown"/> has been called on this gate.</summary>
    public bool IsShuttingDown
    {
        get { lock (_lock) { return _shutdownStarted; } }
    }

    // Tag-builder reused by the +1 admission emit and the -1 release emit so the two points
    // of the pair carry identical tags. The dimensions match the originals in
    // RabbitMqConsumerHost.BuildInFlightTags so the extracted gate is observationally
    // identical to the inlined version.
    private TagList BuildInFlightTags() => new()
    {
        { "messaging.system", "rabbitmq" },
        { "messaging.destination.name", _consumerQueueName },
    };

    /// <summary>
    /// Attempts to admit a new in-flight delivery. Returns <c>false</c> once shutdown has
    /// begun; on success increments the in-flight counter and emits a +1 to the in-flight
    /// gauge with the standard messaging tags.
    /// </summary>
    public bool TryAdmit()
    {
        TagList tags;
        lock (_lock)
        {
            if (_shutdownStarted)
            {
                return false;
            }
            _inFlight++;
            tags = BuildInFlightTags();
        }
        ServiceConnectMeter.AddInFlight(1, tags);
        return true;
    }

    /// <summary>
    /// Pairs with a previously successful <see cref="TryAdmit"/>. Decrements the in-flight
    /// counter, emits the matching -1 to the gauge, and — if shutdown has begun and this
    /// was the last in-flight delivery — completes any pending <see cref="DrainAsync"/>.
    /// </summary>
    public void Release()
    {
        TagList tags;
        TaskCompletionSource? drainTcs;
        lock (_lock)
        {
            tags = BuildInFlightTags();
            int remaining = --_inFlight;
            // Only signal drain when shutdown is in progress AND we're at zero. This avoids
            // pre-creating a TCS for non-shutdown release paths and matches the protocol:
            // BeginShutdown then DrainAsync then per-delivery Release.
            drainTcs = (_shutdownStarted && remaining == 0) ? _drainTcs : null;
        }
        ServiceConnectMeter.AddInFlight(-1, tags);
        drainTcs?.TrySetResult();
    }

    /// <summary>
    /// Marks the gate as shutting down so future <see cref="TryAdmit"/> calls return false.
    /// Idempotent — calling twice has no additional effect.
    /// </summary>
    public void BeginShutdown()
    {
        lock (_lock)
        {
            _shutdownStarted = true;
        }
    }

    /// <summary>
    /// Returns a task that completes once every admitted delivery has been released.
    /// Returns <see cref="Task.CompletedTask"/> immediately if no deliveries are in flight.
    /// Honours <paramref name="cancellationToken"/> via <see cref="Task.WaitAsync(CancellationToken)"/>.
    /// </summary>
    public Task DrainAsync(CancellationToken cancellationToken)
    {
        Task drainTask;
        lock (_lock)
        {
            if (_inFlight == 0)
            {
                return Task.CompletedTask;
            }
            // RunContinuationsAsynchronously: avoids running the drain caller's continuation
            // synchronously inside Release's lock-exit path, which would extend the lock
            // hold time for whatever the drain caller chains next.
            _drainTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainTask = _drainTcs.Task;
        }
        return drainTask.WaitAsync(cancellationToken);
    }
}
