using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface ITransportSettings
    {
        /// <summary>
        /// Delay (in miliseconds) between bus attempts to redeliver message
        /// </summary>
        int RetryDelay { get; set; }

        /// <summary>
        /// Maximum number of retries
        /// </summary>
        int MaxRetries { get; set; }

        /// <summary>
        /// Messaging host
        /// </summary>
        string Host { get; set; }

        /// <summary>
        /// Messaging host username
        /// </summary>
        string Username { get; set; }

        /// <summary>
        /// Messaging host password
        /// </summary>
        string Password { get; set; }

        /// <summary>
        /// Message queue settings
        /// </summary>
        Queue Queue { get; set; }

        string MachineName { get; set; }

        /// <summary>
        /// Custom Error Queue Name
        /// </summary>
        string ErrorQueueName { get; set; }

        /// <summary>
        /// Auditing enabled
        /// </summary>
        bool AuditingEnabled { get; set; }

        /// <summary>
        /// Custom Audit Queue Name
        /// </summary>
        string AuditQueueName { get; set; }

        /// <summary>
        /// Disable sending errors to error queue
        /// </summary>
        bool DisableErrors { get; set; }

        /// <summary>
        /// Custom Heartbeat Queue Name
        /// </summary>
        string HeartbeatQueueName { get; set; }
    }

    public class Queue
    {
        public string Name { get; set; }
        public string RoutingKey { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public bool AutoDelete { get; set; }
        public bool Durable { get; set; }
        public bool Exclusive { get; set; }
        public bool IsReadOnly { get; set; }
    }
}
