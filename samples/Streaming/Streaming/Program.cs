using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;
using Streaming.Messages;

namespace Streaming
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("StreamPublisher");
                x.ScanForMesssageHandlers = true;
            });
            bus.StartConsuming();

            FileStream f = new FileStream(@"logo.bmp", FileMode.Open);

            var bytes = new byte[f.Length];
            f.Read(bytes, 0, Convert.ToInt32(f.Length));
            var stream = bus.CreateStream("StreamConsumer", new StartStreamMessage(Guid.NewGuid())
            {
                Path = @"logoCopy.bmp"
            });

            Console.WriteLine("Writing Bytes");
            stream.Write(bytes);

            Console.WriteLine("Stopping sending");
            stream.Close();

            Console.WriteLine("Done");
            Console.ReadLine();
        }
    }
}
