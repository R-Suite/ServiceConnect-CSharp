namespace ServiceConnect.Client.RabbitMQ;

public static class Retry
{
    // Non-generic overload delegates to the generic one to avoid duplicated retry
    // body logic (L-15). We produce a uniform return type by wrapping the void action.
    public static Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        ArgumentNullException.ThrowIfNull(action);
        return DoAsync<int>(async () => { await action().ConfigureAwait(false); return 0; }, exceptionAction, retryInterval, retryCount);
    }

    public static async Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(exceptionAction);
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    exceptions.Add(callbackEx);
                }

                var delay = CalculateDelay(retryInterval, retry);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }

    private static readonly double MaxDelayMs = TimeSpan.FromMinutes(5).TotalMilliseconds;

    private static TimeSpan CalculateDelay(TimeSpan baseInterval, int retryAttempt)
    {
        // Cap at 52 to prevent double overflow: 2^53 exceeds IEEE-754 double precision
        // and 2^1024 is +Infinity, both of which break TimeSpan construction (R-087).
        // Clamp the raw millisecond value to the ceiling before constructing TimeSpan
        // to avoid OverflowException on huge retry counts.
        var cappedAttempt = Math.Min(retryAttempt, 52);
        var backoff = baseInterval.TotalMilliseconds * Math.Pow(2, cappedAttempt);
        var clampedBackoffMs = Math.Min(backoff, MaxDelayMs);
        var exponentialDelay = TimeSpan.FromMilliseconds(clampedBackoffMs);
        var jitterMs = Random.Shared.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
        var totalDelay = exponentialDelay + TimeSpan.FromMilliseconds(jitterMs);
        return TimeSpan.FromMilliseconds(Math.Min(totalDelay.TotalMilliseconds, MaxDelayMs));
    }
}
