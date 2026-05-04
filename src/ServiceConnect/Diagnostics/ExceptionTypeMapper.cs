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
internal static class ExceptionTypeMapper
{
    public static string Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            _ => exception.GetType().Name,
        };
    }
}
