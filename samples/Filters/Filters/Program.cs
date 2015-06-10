using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Filters.Messages;
using R.MessageBus;

namespace Filters
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Filters.Producer");
                config.SetThreads(10);
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
            });

            while (true)
            {
                Console.WriteLine("1 to successfully filter messages");
                Console.WriteLine("2 to fail filtering messages");
                var result = Console.ReadLine();

                if (result == "1")
                {
                    bus.Send("Filters.Consumer", new FilterMessage(Guid.NewGuid())
                    {
                        FilterFail = false
                    });
                }
                else
                {
                    bus.Send("Filters.Consumer", new FilterMessage(Guid.NewGuid())
                    {
                        FilterFail = true
                    });
                }
            }
        }
    }
}
