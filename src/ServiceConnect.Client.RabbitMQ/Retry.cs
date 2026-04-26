namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Provides asynchronous retry helpers used by the RabbitMQ transport implementation.
/// </summary>
public static class Retry
{
    // Non-generic overload delegates to the generic one to avoid duplicated retry
    // body logic. We produce a uniform return type by wrapping the void action.
    /// <summary>
    /// Executes an asynchronous action with retry behavior.
    /// </summary>
    /// <param name="action">The operation to execute.</param>
    /// <param name="exceptionAction">A callback invoked after a failed attempt.</param>
    /// <param name="retryInterval">The base interval used when calculating retry delays.</param>
    /// <param name="retryCount">The number of retry attempts after the initial attempt.</param>
    /// <param name="cancellationToken">A token used to cancel the retry loop.</param>
    public static Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return DoAsync<int>(async () => { await action().ConfigureAwait(false); return 0; }, exceptionAction, retryInterval, retryCount, shouldRetry: null, cancellationToken);
    }

    /// <summary>
    /// Executes an asynchronous action with retry behavior and a custom retry filter.
    /// </summary>
    /// <param name="action">The operation to execute.</param>
    /// <param name="exceptionAction">A callback invoked after a failed attempt.</param>
    /// <param name="retryInterval">The base interval used when calculating retry delays.</param>
    /// <param name="retryCount">The number of retry attempts after the initial attempt.</param>
    /// <param name="shouldRetry">A predicate that determines whether a thrown exception should be retried.</param>
    /// <param name="cancellationToken">A token used to cancel the retry loop.</param>
    public static Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount, Func<Exception, bool>? shouldRetry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return DoAsync<int>(async () => { await action().ConfigureAwait(false); return 0; }, exceptionAction, retryInterval, retryCount, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes an asynchronous operation that returns a value with retry behavior.
    /// </summary>
    /// <typeparam name="T">The result type produced by the operation.</typeparam>
    /// <param name="action">The operation to execute.</param>
    /// <param name="exceptionAction">A callback invoked after a failed attempt.</param>
    /// <param name="retryInterval">The base interval used when calculating retry delays.</param>
    /// <param name="retryCount">The number of retry attempts after the initial attempt.</param>
    /// <param name="cancellationToken">A token used to cancel the retry loop.</param>
    /// <returns>The value returned by a successful attempt.</returns>
    public static Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount, CancellationToken cancellationToken = default)
    {
        return DoAsync(action, exceptionAction, retryInterval, retryCount, shouldRetry: null, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="action"/> with at most <c>retryCount + 1</c> total attempts
    /// (the first attempt plus up to <paramref name="retryCount"/> retries).
    /// <para>
    /// A <paramref name="retryCount"/> of 0 still runs the action exactly once — the caller's
    /// intent "no retries" implies "no additional attempts", not "no attempt at all".
    /// </para>
    /// <para>
    /// If <paramref name="shouldRetry"/> is provided and returns <c>false</c> for a thrown
    /// exception, the exception is rethrown immediately — no <c>exceptionAction</c>, no delay,
    /// no further attempts. This lets callers mark classes of exceptions (e.g. broker nacks)
    /// as non-retriable while keeping the normal reconnect-retry path for transport errors.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The result type produced by the operation.</typeparam>
    /// <param name="action">The operation to execute.</param>
    /// <param name="exceptionAction">A callback invoked after a failed attempt.</param>
    /// <param name="retryInterval">The base interval used when calculating retry delays.</param>
    /// <param name="retryCount">The number of retry attempts after the initial attempt.</param>
    /// <param name="shouldRetry">A predicate that determines whether a thrown exception should be retried.</param>
    /// <param name="cancellationToken">A token used to cancel the retry loop.</param>
    /// <returns>The value returned by a successful attempt.</returns>
    public static async Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount, Func<Exception, bool>? shouldRetry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(exceptionAction);
        if (retryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        List<Exception>? exceptions = null;

        for (int attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // OCE is contractually non-retryable regardless of which token cancelled it.
                throw;
            }
            catch (Exception ex)
            {
                if (shouldRetry != null && !shouldRetry(ex))
                {
                    throw;
                } (exceptions ??= []).Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    (exceptions ??= []).Add(callbackEx);
                }

                if (attempt < retryCount)
                {
                    var delay = CalculateDelay(retryInterval, attempt);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new AggregateException(exceptions ?? []);
    }

    private static readonly double MaxDelayMs = TimeSpan.FromMinutes(5).TotalMilliseconds;

    private static TimeSpan CalculateDelay(TimeSpan baseInterval, int retryAttempt)
    {
        // Cap at 52 to prevent double overflow: 2^53 exceeds IEEE-754 double precision
        // and 2^1024 is +Infinity, both of which break TimeSpan construction.
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
