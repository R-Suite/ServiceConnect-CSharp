using System;
using System.Collections.Generic;
using ServiceConnect;
using Streaming.Messages;

namespace Streaming.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("StreamConsumer");
                x.PurgeQueuesOnStart();
                x.SetHost("localhost");
                x.SetThreads(10);
            });

            Console.ReadLine();
        }
    }
}
