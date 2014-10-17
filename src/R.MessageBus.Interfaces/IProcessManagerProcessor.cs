namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerProcessor
    {
        void ProcessMessage<T>(string message, IConsumeContext context) where T : Message;
    }
}