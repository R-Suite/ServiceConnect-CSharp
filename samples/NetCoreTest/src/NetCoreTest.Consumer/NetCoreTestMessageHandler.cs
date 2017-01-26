using System;
using System.Threading;
using NetCoreTest.Messages;
using ServiceConnect.Interfaces;

namespace NetCoreTest.Consumer
{
    public class NetCoreTestMessageHandler : IMessageHandler<NetCoreTestMessage>
    {
        public void Execute(NetCoreTestMessage command)
        {
            Thread.Sleep(100);
            Console.WriteLine("{0}: Consumer 1 Received Message - {1}", Thread.CurrentThread.ManagedThreadId, command.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
