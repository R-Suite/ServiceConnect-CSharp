using System;
using System.Threading;
using Kafka.Messages;
using ServiceConnect.Interfaces;

namespace Kafka.Consumer
{
    public class NetCoreTestMessageHandler : IMessageHandler<NetCoreTestMessage>
    {
        public void Execute(NetCoreTestMessage command)
        {
            if (command.SerialNumber % 1000 == 0)
            {
                Console.WriteLine("{0}: Consumer 1 Received Message - {1}", command.SerialNumber, command.CorrelationId);
            }
        }

        public IConsumeContext Context { get; set; }
    }
}
