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
            Console.WriteLine("+++++++++++++++++++++++++++++++++++++ {0}: Handler", Thread.CurrentThread.ManagedThreadId, command.CorrelationId);
            //Thread.Sleep(1000);
        }

        public IConsumeContext Context { get; set; }
    }
}