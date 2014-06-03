using R.MessageBus.Interfaces;

namespace R.MessageBus.Settings
{
    public class TransportSettings : ITransportSettings
    {
        public int RetryDelay { get; set; }
        public int MaxRetries { get; set; }
        public string Host { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool NoAck { get; set; }
        public Queue Queue { get; set; }
        public string MachineName { get; set; }
    }
}
