namespace R.MessageBus.Interfaces
{
    public interface IConsumer
    {
        void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null);
        void StopConsuming();
        void Dispose();
        void ConsumeMessageType(string messageTypeName);
    }
}