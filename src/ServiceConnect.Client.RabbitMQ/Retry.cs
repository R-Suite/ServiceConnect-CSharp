namespace ServiceConnect.Client.RabbitMQ;

public static class Retry
{
    public static async Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
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

    public static async Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
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

    private static TimeSpan CalculateDelay(TimeSpan baseInterval, int retryAttempt)
    {
        var exponentialDelay = TimeSpan.FromMilliseconds(baseInterval.TotalMilliseconds * Math.Pow(2, retryAttempt));
        var jitterMs = Random.Shared.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
        var totalDelay = exponentialDelay + TimeSpan.FromMilliseconds(jitterMs);
        return TimeSpan.FromMilliseconds(Math.Min(totalDelay.TotalMilliseconds, TimeSpan.FromMinutes(5).TotalMilliseconds));
    }
}
