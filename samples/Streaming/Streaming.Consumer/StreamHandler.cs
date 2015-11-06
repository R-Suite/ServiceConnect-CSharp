using System;
using System.IO;
using ServiceConnect.Interfaces;
using Streaming.Messages;

namespace Streaming.Consumer
{
    public class StreamHandler : IStreamHandler<StartStreamMessage>
    {
        public IMessageBusReadStream Stream { get; set; }

        public void Execute(StartStreamMessage message)
        {
            Console.WriteLine("Reading stream - {0}", message.Path);
            var ms = new FileStream(message.Path, FileMode.Create);
            
            while (!Stream.IsComplete())
            {
                var bytes = Stream.Read();
                if (bytes.Length > 0)
                {
                    Console.WriteLine("Writing...");
                    ms.Write(bytes, 0, bytes.Length);
                }
            }
            
            ms.Close();

            Console.WriteLine("Stream Read - {0}", message.Path);
        }
    }
}