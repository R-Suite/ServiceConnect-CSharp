using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Filters.Messages;
using ServiceConnect;
using ServiceConnect.Container.StructureMap;
using StructureMap;

namespace Filters
{
    class Program
    {
        static void Main(string[] args)
        {
            var container = new Container();

            container.Configure(x => x.For<Filter1>().Use<Filter1>().Ctor<string>("p1").Is("test dependency injection"));

            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetContainer(container);
                config.SetQueueName("Filters.Producer");
                config.SetThreads(10);
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
                config.OutgoingFilters = new List<Type>
                {
                    typeof(Filter1),
                    typeof(Filter2)
                };
            });

            while (true)
            {
                Console.WriteLine("1 to successfully filter messages");
                Console.WriteLine("2 for consumer fail filtering messages");
                Console.WriteLine("3 for producer fail filtering messages");
                var result = Console.ReadLine();

                if (result == "1")
                {
                    bus.Send("Filters.Consumer", new FilterMessage(Guid.NewGuid())
                    {
                        ConsumerFilterFail = false
                    });
                }
                else if (result == "2")
                {
                    bus.Send("Filters.Consumer", new FilterMessage(Guid.NewGuid())
                    {
                        ConsumerFilterFail = true
                    });
                }
                else
                {
                    bus.Send("Filters.Consumer", new FilterMessage(Guid.NewGuid())
                    {
                        ProducerFilterFail = true
                    });
                }
            }
        }
    }
}
