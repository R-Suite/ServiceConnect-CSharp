using System;
using System.IO;
using System.Threading;
using PointToPoint.Messages;
using ServiceConnect.Interfaces;

namespace PointToPoint.Consumer
{
    public class MessageDeduplicationHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            //Thread.Sleep(400);
            Console.WriteLine("{0}: Consumer 1 Received Message - {1}", Thread.CurrentThread.ManagedThreadId, command.CorrelationId);

            //using (StreamWriter writer =
            //    new StreamWriter("check.txt", true))
            //{
            //    writer.WriteLine("{0} - {1}", command.SerialNumber, command.CorrelationId);
            //}
        }

        public IConsumeContext Context { get; set; }
    }
}