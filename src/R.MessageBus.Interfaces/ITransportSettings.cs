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
        /// When set to true, messages are considered automatically acknowledged as soon as they have been delivered.
        /// When set to false, messages must be acknowledged manually with basic.ack
        /// </summary>
        bool NoAck { get; set; }

        /// <summary>
        /// Message queue settings
        /// </summary>
        Queue Queue { get; set; }

        string MachineName { get; set; }
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
