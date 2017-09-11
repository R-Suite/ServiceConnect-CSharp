using System;
using System.Threading;
using Aggregator.Messages;
using ServiceConnect;

namespace Aggregator
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Publisher ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("Aggregator.Publisher");
                x.PurgeQueuesOnStart();
                x.SetAuditingEnabled(true);
                x.SetHost("localhost");
            });

            Console.WriteLine("Press enter");
            Console.ReadLine();

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
