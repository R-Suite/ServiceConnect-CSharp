namespace R.MessageBus.Interfaces
{
    public interface IMessageSerializer
    {
        object Deserialize(string typeName, string messageJson);
        string Serialize<T>(T message);
    }
}
