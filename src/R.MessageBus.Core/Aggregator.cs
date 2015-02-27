using System;
using System.Collections.Generic;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    /// <summary>
    /// Define aggregated message handlers
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class Aggregator<T> where T : Message
    {
        /// <summary>
        /// Timeout for aggregating messages.
        /// When the timeout is reached, the current batch of messages is dispatched 
        /// to the handler (regardless of the batch size).
        /// </summary>
        /// <returns></returns>
        public virtual TimeSpan Timeout()
        {
            return default(TimeSpan);
        }

        /// <summary>
        /// Max batch size of aggregated messages
        /// </summary>
        /// <returns></returns>
        public virtual int BatchSize()
        {
            return 0;
        }

        public abstract void Execute(IList<T> messages);
    }
}