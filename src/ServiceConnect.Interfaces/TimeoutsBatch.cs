using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    public class TimeoutsBatch
    {
        /// <summary>
        /// Timeouts due to be triggered
        /// </summary>
        public IList<TimeoutData> DueTimeouts { get; set; }

        /// <summary>
        /// The next time to query peristance store for due timeouts
        /// </summary>
        public DateTime NextQueryTime { get; set; }
    }
}
