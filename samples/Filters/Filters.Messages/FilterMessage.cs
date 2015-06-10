using System;
using R.MessageBus.Interfaces;

namespace Filters.Messages
{
    public class FilterMessage : Message
    {
        public FilterMessage(Guid correlationId) : base(correlationId)
        {
        }

        public bool ConsumerFilterFail { get; set; }
        public bool ProducerFilterFail { get; set; }
        public string FilterModifiedValue { get; set; }
    }
}
