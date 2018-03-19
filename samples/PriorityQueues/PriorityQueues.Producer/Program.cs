using System;
using PriorityQueues.Messages;
using ServiceConnect;

namespace PriorityQueues.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");

            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(MyMessage), "PriorityQueues.Consumer");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send messages");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);

                for (int i = 0; i < 100; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Send("PriorityQueues.Consumer", new MyMessage(id)
                    {
                        Name = "Low Priority Message"
                    });
                }

                bus.Send("PriorityQueues.Consumer", new MyMessage(Guid.NewGuid())
                {
                    Name = "Hi Priority Message"
                }, new System.Collections.Generic.Dictionary<string, string> {{"Priority", "9"}});

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
