using System;
using ContentRouting.Messages;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ContentRouting.Consumer2
{
    [RoutingKey("#")]
    public class MyMessageMessageHandler : IMessageHandler<MyMessage>
    {
        public void Execute(MyMessage message)
        {
            Console.WriteLine("Consumer 2 (catch all) Received Message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
