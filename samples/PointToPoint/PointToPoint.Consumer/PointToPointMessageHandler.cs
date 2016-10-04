using System;
using System.Threading;
using PointToPoint.Messages;
using ServiceConnect.Interfaces;

namespace PointToPoint.Consumer
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            Thread.Sleep(100);
            Console.WriteLine("{0}: Consumer 1 Received Message - {1}", Thread.CurrentThread.ManagedThreadId, command.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}