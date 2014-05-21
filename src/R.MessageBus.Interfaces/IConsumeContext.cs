using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IConsumeContext
    {
        IBus Bus { set; }
        IDictionary<string, object> Headers { get; set; }
        void Reply<TReply>(TReply message)  where TReply : Message;
    }
}