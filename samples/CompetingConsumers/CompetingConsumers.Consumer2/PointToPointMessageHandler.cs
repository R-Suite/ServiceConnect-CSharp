using System;
using CompetingConsumers.Messages;
using ServiceConnect.Interfaces;

namespace CompetingConsumers.Consumer2
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            Console.WriteLine("Consumer 2 Received Message - {0}", command.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}