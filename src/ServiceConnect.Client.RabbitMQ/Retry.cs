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
                await Task.Delay(retryInterval).ConfigureAwait(false);
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
                await Task.Delay(retryInterval).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }
}
