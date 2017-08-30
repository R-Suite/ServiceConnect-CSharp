using System;
using NetCoreTest.Messages;
using ServiceConnect;

namespace NetCoreTest.Producer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(NetCoreTestMessage), "NetCoreTest.Consumer");
                config.SetHost("localhost");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);

                for (int i = 0; i < 30; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Send("NetCoreTest.Consumer", new NetCoreTestMessage(id)
                    {
                        Data = new byte[10000],
                        SerialNumber = i
                    });
                    //Console.ReadLine();
                }

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
