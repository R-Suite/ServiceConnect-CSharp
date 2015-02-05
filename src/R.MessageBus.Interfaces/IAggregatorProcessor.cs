namespace R.MessageBus.Interfaces
{
    public interface IAggregatorProcessor
    {
        void ProcessMessage<T>(string message) where T : Message;
    }

}