namespace ServiceConnect.Interfaces.Exceptions;

public class RequestTimeoutException : ServiceConnectException
{
    public Guid CorrelationId { get; }
    public TimeSpan Elapsed { get; }
    public RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
        : base($"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms")
    {
        CorrelationId = correlationId;
        Elapsed = elapsed;
    }
}
