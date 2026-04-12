namespace ServiceConnect.Interfaces.Exceptions;

public sealed class RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
    : ServiceConnectException($"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms")
{
    public Guid CorrelationId { get; } = correlationId;
    public TimeSpan Elapsed { get; } = elapsed;
}
