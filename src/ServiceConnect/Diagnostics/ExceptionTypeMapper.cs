namespace ServiceConnect.Diagnostics;

/// <summary>
/// Maps an exception to a stable, low-cardinality string suitable for the OpenTelemetry
/// <c>error.type</c> tag on metric records. Uses an allow-list for common .NET exception
/// types and falls back to <see cref="Exception.GetType"/>'s short name.
/// </summary>
/// <remarks>
/// The exception <i>message</i> is never used — message text is unbounded cardinality and
/// would explode metric series counts.
/// </remarks>
public static class ExceptionTypeMapper
{
    // FullName strings for RabbitMQ.Client exceptions. Kept as constants so the core
    // library stays transport-agnostic (no direct reference to RabbitMQ.Client) while
    // still producing stable, human-readable error.type values. AlreadyClosedException
    // extends OperationInterruptedException, so its string must be checked first.
    private const string AlreadyClosedFqn = "RabbitMQ.Client.Exceptions.AlreadyClosedException";
    private const string BrokerUnreachableFqn = "RabbitMQ.Client.Exceptions.BrokerUnreachableException";
    private const string PublishExceptionFqn = "RabbitMQ.Client.Exceptions.PublishException";
    private const string OperationInterruptedFqn = "RabbitMQ.Client.Exceptions.OperationInterruptedException";

    /// <summary>
    /// Maps the exception to the corresponding stable, low-cardinality string used as the
    /// <c>error.type</c> tag value on metric records.
    /// </summary>
    public static string Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            _ => MapByFullName(exception),
        };
    }

    private static string MapByFullName(Exception exception)
    {
        // Walk the type hierarchy so that subclasses of the known AMQP types are also
        // matched. AlreadyClosedException must be checked before OperationInterruptedException
        // because it is a subclass — FullName comparison is exact, so we climb the chain.
        var type = exception.GetType();
        while (type is not null)
        {
            var fqn = type.FullName;
            if (string.Equals(fqn, AlreadyClosedFqn, StringComparison.Ordinal))
            {
                return "channel_closed";
            }
            if (string.Equals(fqn, BrokerUnreachableFqn, StringComparison.Ordinal))
            {
                return "broker_unreachable";
            }
            if (string.Equals(fqn, PublishExceptionFqn, StringComparison.Ordinal))
            {
                return "publish_nacked";
            }
            if (string.Equals(fqn, OperationInterruptedFqn, StringComparison.Ordinal))
            {
                return "broker_interrupted";
            }
            type = type.BaseType;
        }
        return exception.GetType().Name;
    }
}
