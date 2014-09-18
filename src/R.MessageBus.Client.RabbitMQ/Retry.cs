using System;
using System.Collections.Generic;
using System.Threading;

namespace R.MessageBus.Client.RabbitMQ
{
    public static class Retry
    {
        public static void Do(Action action, Action<Exception> exceptionAction, TimeSpan retryInterval)
        {
            while (true)
            {
                try
                {
                    while (true)
                    {
                        action();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    exceptionAction(ex);
                    Thread.Sleep(retryInterval);
                }
            }
        }

        public static void Do(Action action, Action<Exception> exceptionAction, TimeSpan retryInterval, int retryCount)
        {
            var exceptions = new List<Exception>();

            for (int retry = 0; retry < retryCount; retry++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    exceptionAction(ex);
                    Thread.Sleep(retryInterval);
                }
            }

            throw new AggregateException(exceptions);
        }

        public static T Do<T>(Func<T> action, Action<Exception> exceptionAction, TimeSpan retryInterval, int retryCount)
        {
            var exceptions = new List<Exception>();

            for (int retry = 0; retry < retryCount; retry++)
            {
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    exceptionAction(ex);
                    Thread.Sleep(retryInterval);
                }
            }

            throw new AggregateException(exceptions);
        }
    }
}