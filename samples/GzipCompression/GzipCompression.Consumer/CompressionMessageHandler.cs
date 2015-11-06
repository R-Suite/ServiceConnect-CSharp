using System;
using GzipCompression.Messages;
using ServiceConnect.Interfaces;

namespace GzipCompression.Consumer
{
    public class CompressionMessageHandler : IMessageHandler<CompressionMessage>
    {
        public void Execute(CompressionMessage message)
        {
            Console.WriteLine(message.Data);
        }

        public IConsumeContext Context { get; set; }
    }
}
