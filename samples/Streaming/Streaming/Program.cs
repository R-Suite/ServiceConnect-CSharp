using System;
using System.Collections.Generic;
using System.IO;
using R.MessageBus;
using Streaming.Messages;

namespace Streaming
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("StreamPublisher");
                x.PurgeQueuesOnStart();
            });

            FileStream f = new FileStream(@"logo.bmp", FileMode.Open);
            //FileStream f = new FileStream(@"TestPackage.nupkg", FileMode.Open);

            var stream = bus.CreateStream("StreamConsumer", new StartStreamMessage(Guid.NewGuid())
            {
                Path = @"logoCopy.bmp"
                //Path = @"TestPackageCopy.nupkg"
            });

            byte[] buffer = new byte[1000];
            int read;
            while ((read = f.Read(buffer, 0, buffer.Length)) > 0)
            {
                Console.WriteLine("Writing Bytes");
                stream.Write(buffer, 0, read);
            }

            Console.WriteLine("Stopping sending");
            stream.Close();

            Console.WriteLine("Done");
            Console.ReadLine();
        }
    }
}
