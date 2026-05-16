namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by request-reply paths (<see cref="IBus.SendRequestAsync"/>,
/// <see cref="IBus.SendRequestMultiAsync"/>, <see cref="IBus.PublishRequestAsync"/>)
/// when an outgoing filter returned <see cref="FilterAction.Stop"/>, blocking the
/// request before it reached the transport. Distinct from a raw
/// <see cref="System.InvalidOperationException"/> (which would conflate filter-stop with
/// state-misuse) and from <see cref="OperationCanceledException"/> (which signals caller
/// cancellation, not deliberate filter rejection).
/// </summary>
/// <remarks>
/// <b>Fire-and-forget paths do NOT throw this exception.</b> When an outgoing filter
/// returns <see cref="FilterAction.Stop"/> on <see cref="IBus.PublishAsync"/>,
/// <see cref="IBus.SendAsync"/>, <see cref="IBus.SendToManyAsync"/>, or
/// <see cref="IBus.RouteAsync"/>, the call returns silently and the message is dropped
/// before the transport publish. Callers that need a typed signal for filter-stop on those
/// paths should either route through a request-reply variant or instrument the filter
/// itself (a custom filter can log or emit a counter on its
/// <see cref="FilterAction.Stop"/> return path).
/// </remarks>
public sealed class OutgoingFiltersBlockedException : ServiceConnectException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutgoingFiltersBlockedException"/> class.
    /// </summary>
    public OutgoingFiltersBlockedException() { }

    /// <summary>
    /// Initializes a new instance with a human-readable message.
    /// </summary>
    public OutgoingFiltersBlockedException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance with a human-readable message and inner exception.
    /// </summary>
    public OutgoingFiltersBlockedException(string message, Exception? innerException)
        : base(message, innerException) { }
}
