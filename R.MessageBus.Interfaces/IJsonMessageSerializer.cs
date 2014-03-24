namespace R.MessageBus.Interfaces
{
    public interface IJsonMessageSerializer
    {
        object Deserialize(string message);

        string Serialize(object message);
    }
}
