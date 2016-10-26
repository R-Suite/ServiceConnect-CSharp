using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    /// <summary>
    /// Holds timeout information.
    /// </summary>
    public class TimeoutData
    {
        /// <summary>
        /// Timeout id
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The address of the client who requested the timeout.
        /// </summary>
        public string Destination { get; set; }

        /// <summary>
        /// The saga ID.
        /// </summary>
        public Guid ProcessManagerId { get; set; }

        /// <summary>
        /// The time at which the timeout expires.
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// Store the headers to preserve them across timeouts.
        /// </summary>
        public IDictionary<string, object> Headers { get; set; }

        /// <summary>
        /// Mark processed tiomouts as dispatched to prevent multiple dispatch of the same timeout
        /// </summary>
        public bool Locked { get; set; }
    }
}