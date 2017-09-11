using System;
using AsyncMessagehandlers.Messages;
using ServiceConnect.Interfaces;

namespace AsyncMessageHandlers.Consumers
{
    public class SyncMessageHandler : IMessageHandler<AsyncMessage>
    {
        public void Execute(AsyncMessage message)
        {
            Console.WriteLine("Executing SyncMessageHandler");
        }

        public IConsumeContext Context { get; set; }
    }
}