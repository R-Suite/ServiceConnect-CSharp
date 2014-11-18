using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class HeartbeatMessage : Message
    {
        public HeartbeatMessage(Guid correlationId) : base(correlationId)
        {
        }

        public DateTime Timestamp { get; set; }
        public string Name { get; set; }
        public string Location { get; set; }
        public double LatestCpu { get; set; }
        public double LatestMemory { get; set; }
    }
}