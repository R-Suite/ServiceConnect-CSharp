using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PointToPoint.ZeroMQ.Messages;
using R.MessageBus.Interfaces;

namespace PointToPoint.ZeroMQ.Consumer
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            Console.WriteLine("Received message - {0} {1}", command.CorrelationId, DateTime.Now);
        }

        public IConsumeContext Context { get; set; }
    }
}
