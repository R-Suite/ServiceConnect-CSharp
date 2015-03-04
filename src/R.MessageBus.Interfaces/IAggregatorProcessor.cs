using System;

namespace R.MessageBus.Interfaces
{
    public delegate void BatchProcessedHandler(Type messageType, EventArgs e);

    /// <summary>
    /// Aggregate messages into batches of a predefined size
    /// and pass them to relevant handlers
    /// </summary>
    public interface IAggregatorProcessor
    {
        event BatchProcessedHandler BatchProcessed;
        void ProcessMessage<T>(string message) where T : Message;
    }
}