using System;
using ContentRouting.Messages;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ContentRouting.Consumer1
{
    [RoutingKey("routingkey0")]
    public class MyMessageMessageHandler : IMessageHandler<MyMessage>
    {
        public void Execute(MyMessage message)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }

    [RoutingKey("routingkey0")]
    public class MyBaseMessageMessageHandler : IMessageHandler<MyBaseMessage>
    {
        public void Execute(MyBaseMessage message)
        {
            Console.WriteLine("Consumer 1 Received Base Message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }

    public class MyMessageMessageHandlerNoRoutingKey : IMessageHandler<MyMessage>
    {
        public void Execute(MyMessage message)
        {
            Console.WriteLine("Consumer 1 (MyMessageMessageHandlerNoRoutingKey) Received Message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
