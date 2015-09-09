using System;
using PointToPoint.ZeroMQ.Messages;
using R.MessageBus.Interfaces;

namespace PointToPoint.ZeroMQ.Consumer2
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            //throw new Exception("test2");
            Console.WriteLine("Received message - {0} {1}", command.Count, DateTime.Now);
        }

        public IConsumeContext Context { get; set; }
    }
}
