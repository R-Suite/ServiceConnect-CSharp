namespace R.MessageBus.Interfaces
{
    public interface IBus
    {
        void StartConsuming(string configPath, string endPoint, string queue = null);
    }
}