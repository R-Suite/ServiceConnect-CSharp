namespace R.MessageBus.Interfaces
{
    public interface IBus
    {
        IConfiguration Configuration { get; set; }
        void StartConsuming(string configPath, string endPoint, string queue = null);
    }
}