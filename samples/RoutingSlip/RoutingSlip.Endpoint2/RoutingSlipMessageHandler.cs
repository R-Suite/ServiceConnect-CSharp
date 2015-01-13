using System;
using R.MessageBus.Interfaces;
using RoutingSlip.Messages;

namespace RoutingSlip.Endpoint2
{
    public class RoutingSlipMessageHandler : IMessageHandler<RoutingSlipMessage>
    {
        public void Execute(RoutingSlipMessage message)
        {
            Console.WriteLine("Endpoint2 received message - {0}", message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
