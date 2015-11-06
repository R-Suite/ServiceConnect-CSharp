using System;
using PolymorphicMessages.Messages;
using ServiceConnect.Interfaces;

namespace PolymorphicMessages.Consumer
{
    public class BaseTypeHandler : IMessageHandler<BaseType>
    {
        public void Execute(BaseType message)
        {
            Console.WriteLine("");
            Console.WriteLine("Received: {0} - {1}", message.GetType().Name, message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}
