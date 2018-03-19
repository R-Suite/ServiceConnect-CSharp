using System;
using System.Threading;
using PriorityQueues.Messages;
using ServiceConnect.Interfaces;

namespace PriorityQueues.Consumer
{
    public class MyMessageHandler : IMessageHandler<MyMessage>
    {
        public void Execute(MyMessage message)
        {
            Thread.Sleep(100);
            Console.WriteLine("{0}: Consumer Received Message - {1}",
                Thread.CurrentThread.ManagedThreadId, message.Name);
        }

        public IConsumeContext Context { get; set; }
    }
}
