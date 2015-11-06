using System;
using ServiceConnect.Interfaces;

namespace CustomIoCContainer
{
    public class MyMessageHandler : IMessageHandler<MyMessage>
    {
        public void Execute(MyMessage message)
        {
            Console.Write("In MyMessageHandler. message.CorrelationId= {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
