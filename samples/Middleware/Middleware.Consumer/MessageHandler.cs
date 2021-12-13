using System;
using Middleware.Messages;
using ServiceConnect.Interfaces;

namespace Middleware.Consumer
{
    public class MessageHandler : IMessageHandler<MiddlewareMessage>
    {
        public void Execute(MiddlewareMessage message)
        {
            Console.WriteLine("Inside consumer - Value = " + message.Value);
        }

        public IConsumeContext Context { get; set; }
    }
}