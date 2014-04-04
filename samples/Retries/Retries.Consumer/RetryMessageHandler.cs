using System;
using R.MessageBus.Interfaces;
using Retries.Messages;

namespace Retries.Consumer
{
    public class RetryMessageHandler : IMessageHandler<RetryMessage>
    {
        public void Execute(RetryMessage message)
        {
            Console.WriteLine("Handling message - {0}", message.CorrelationId);

            throw new NotImplementedException();
        }
    }
}
