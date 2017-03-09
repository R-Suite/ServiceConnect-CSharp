using System;
using PointToPoint.Messages;
using ServiceConnect;

namespace PointToPoint.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(PointToPointMessage), "MessageDeduplication.Consumer");
                config.SetHost("localhost");
                config.SetQueueName("MessageDeduplication.Producer");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);

                for (int i = 0; i < 1000000; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Send(new PointToPointMessage(id)
                    {
                        //Data = new byte[10000],
                        SerialNumber = i
                    });
                    //Console.ReadLine();
                }

                Console.WriteLine("End: {0}", DateTime.Now);

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
