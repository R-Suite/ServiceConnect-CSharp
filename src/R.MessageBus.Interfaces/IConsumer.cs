using System;

namespace R.MessageBus.Interfaces
{
    public interface IConsumer : IDisposable
    {
        void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null);
        void StopConsuming();
        void ConsumeMessageType(string messageTypeName);
        string Type { get; }
    }
}