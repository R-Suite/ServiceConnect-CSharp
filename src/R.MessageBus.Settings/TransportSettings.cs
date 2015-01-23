using System.Collections.Generic;
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
        public Queue Queue { get; set; }
        public string MachineName { get; set; }
        public string ErrorQueueName { get; set; }
        public bool AuditingEnabled { get; set; }
        public string AuditQueueName { get; set; }
        public bool DisableErrors { get; set; }
        public string HeartbeatQueueName { get; set; }
        public IDictionary<string, object> ClientSettings { get; set; }
    }
}
