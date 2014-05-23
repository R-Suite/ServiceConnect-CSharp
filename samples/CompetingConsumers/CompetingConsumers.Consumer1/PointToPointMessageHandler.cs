using System;
using CompetingConsumers.Messages;
using R.MessageBus.Interfaces;

namespace CompetingConsumers.Consumer1
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", command.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}