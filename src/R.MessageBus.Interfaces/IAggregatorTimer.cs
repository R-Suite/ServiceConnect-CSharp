using System;

namespace R.MessageBus.Interfaces
{
    public interface IAggregatorTimer : IDisposable
    {
        void StartTimer<T>(TimeSpan timeout);
    }
}