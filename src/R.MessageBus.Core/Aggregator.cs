using System;
using System.Collections.Generic;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public abstract class Aggregator<T> where T : Message
    {
        public virtual TimeSpan Timeout()
        {
            return default(TimeSpan);
        }

        public virtual int BatchSize()
        {
            return 0;
        }

        public abstract void Execute(IList<T> messages);
    }
}