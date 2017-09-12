using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncProcessManager.Messages;
using ServiceConnect;

namespace AsyncProcessManager.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(StartMessage), "AsyncProcessManager.ProcessManager");
                config.SetHost("localhost");
            });

            Console.WriteLine("Press enter to send message");
            while (true)
            {
                Console.ReadLine();
                var id = Guid.NewGuid();
                bus.Send(new StartMessage(id));
                Console.WriteLine("Message sent");
            }
        }
    }
}
