using System;

namespace ServiceConnect.Filters.MessageDeduplication
{
    public class ProcessedMessage
    {
        public Guid Id { get; set; }
        public DateTime ExpiryDateTime { get; set; }
    }
}