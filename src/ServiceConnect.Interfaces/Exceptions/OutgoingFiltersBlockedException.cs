namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by an outgoing send path — <see cref="IBus.PublishAsync"/>, <see cref="IBus.SendAsync"/>,
/// <see cref="IBus.SendToManyAsync"/>, <see cref="IBus.RouteAsync"/>, <see cref="IBus.SendRequestAsync"/>,
/// <see cref="IBus.SendRequestMultiAsync"/>, or <see cref="IBus.PublishRequestAsync"/> — when an outgoing
/// filter returned <see cref="FilterAction.Stop"/>, blocking the message before it reached the transport.
/// Distinct from a raw <see cref="System.InvalidOperationException"/> (which would conflate filter-stop with
/// state-misuse) and from <see cref="OperationCanceledException"/> (which signals caller cancellation, not
/// deliberate filter rejection).
/// </summary>
/// <remarks>
/// Every outgoing send path throws this when an outgoing filter returns <see cref="FilterAction.Stop"/>,
/// so a filter-blocked send surfaces as a typed exception rather than a silent drop. A custom filter can
/// additionally log or emit a counter on its <see cref="FilterAction.Stop"/> return path.
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
