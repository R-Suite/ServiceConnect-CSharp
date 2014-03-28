namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerProcessor
    {
        void ProcessMessage<T>(T message) where T : Message;
    }
}