namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a request/reply operation that exceeded its timeout.
/// </summary>
/// <param name="correlationId">The correlation id of the timed-out request.</param>
/// <param name="elapsed">The time spent waiting for replies.</param>
public sealed class RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
    : ServiceConnectException(System.FormattableString.Invariant(
        $"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms"))
{
    /// <summary>
    /// Gets the correlation id of the timed-out request.
    /// </summary>
    public Guid CorrelationId { get; } = correlationId;

    /// <summary>
    /// Gets the elapsed waiting time.
    /// </summary>
    public TimeSpan Elapsed { get; } = elapsed;
}
