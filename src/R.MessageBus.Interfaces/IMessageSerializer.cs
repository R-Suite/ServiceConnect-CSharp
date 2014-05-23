namespace R.MessageBus.Interfaces
{
    public interface IMessageSerializer
    {
        object Deserialize(string message);
        string Serialize(object message);
    }
}
