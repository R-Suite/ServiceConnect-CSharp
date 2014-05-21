using System;
using System.Collections.Generic;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class ConsumeContext : IConsumeContext
    {
        private IBus _bus;

        public IBus Bus
        {
            set { _bus = value; }
        }

        public IDictionary<string, object> Headers { get; set; }

        public void Reply<TReply>(TReply message)  where TReply : Message
        {
            if (Headers.ContainsKey("SourceAddress"))
            {
                _bus.Send(Headers["SourceAddress"].ToString(), message);
            }
            else
            {
                throw new ArgumentException("SourceAddress not found in message headers.");
            }
        }


    }
}
