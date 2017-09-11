using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncMessagehandlers.Messages;
using ServiceConnect;

namespace AsyncMessageHandlers.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(AsyncMessage), "AsyncTest.Consumer");
                config.SetHost("localhost");
                config.AutoStartConsuming = false;
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);

                for (int i = 0; i < 300; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Send(new AsyncMessage(id));
                    Console.ReadLine();
                }

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
