using System;

namespace R.MessageBus.Interfaces
{
    /// <summary>
    /// Aggregate messages into batches of a predefined size
    /// and pass them to relevant handlers
    /// </summary>
    public interface IAggregatorProcessor : IDisposable
    {
        void ProcessMessage<T>(string message) where T : Message;
        void StartTimer<T>(TimeSpan timeout);
        void ResetTimer();
    }
}