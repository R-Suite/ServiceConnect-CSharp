using System;
using ServiceConnect.Interfaces;
using RoutingSlip.Messages;

namespace RoutingSlip.Endpoint1
{
    public class RoutingSlipMessageHandler : IMessageHandler<RoutingSlipMessage>
    {
        public void Execute(RoutingSlipMessage message)
        {
            Console.WriteLine("Endpoint1 received message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
