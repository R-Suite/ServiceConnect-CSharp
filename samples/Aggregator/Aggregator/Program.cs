using System;
using System.Threading;
using Aggregator.Messages;
using R.MessageBus;

namespace Aggregator
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("Aggregator.Publisher");
                x.SetHost("lonappdev04");
                x.PurgeQueuesOnStart();
            });

            for (int i = 0; i < 1000; i++)
            {
                bus.Send("Aggregator.Consumer", new TestMessage(Guid.NewGuid())
                {
                    Num = i + 1
                });
                Thread.Sleep(10);
            }

            Console.WriteLine("*********** Complete ***********");

            Console.ReadLine();
        }
    }
}
