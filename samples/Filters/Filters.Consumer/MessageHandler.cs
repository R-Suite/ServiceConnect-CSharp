using System;
using Filters.Messages;
using R.MessageBus.Interfaces;

namespace Filters.Consumer
{
    public class MessageHandler : IMessageHandler<FilterMessage>
    {
        public void Execute(FilterMessage message)
        {
            Console.WriteLine("Inside consumer - Value = " + message.FilterModifiedValue);
        }

        public IConsumeContext Context { get; set; }
    }
}