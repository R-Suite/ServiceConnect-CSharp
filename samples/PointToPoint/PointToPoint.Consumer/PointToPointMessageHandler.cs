using System;
using PointToPoint.Messages;
using R.MessageBus.Interfaces;

namespace PointToPoint.Consumer
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            //Console.WriteLine("Received message - {0}", command.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}