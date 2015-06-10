using System;
using R.MessageBus.Interfaces;

namespace Filters.Messages
{
    public class FilterMessage : Message
    {
        public FilterMessage(Guid correlationId) : base(correlationId)
        {
        }

        public bool FilterFail { get; set; }

        public string FilterModifiedValue { get; set; }
    }
}
