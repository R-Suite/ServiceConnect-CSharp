namespace R.MessageBus.Interfaces
{
    public interface IConsumer
    {
        void StartConsuming(ConsumerEventHandler messageReceived, string messageTypeName, string queueName, bool? exclusive = null);
        void StopConsuming();
        void Dispose();
    }
}