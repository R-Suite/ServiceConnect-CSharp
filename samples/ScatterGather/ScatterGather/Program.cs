using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;
using R.MessageBus.Interfaces;
using ScatterGather.Messages;

namespace ScatterGather
{
    class Program
    {
        private static IBus _bus;

        static void Main(string[] args)
        {
            Console.WriteLine("*********** Publisher ***********");
            _bus = Bus.Initialize(config =>
            {

            });
            _bus.StartConsuming();

            while (true)
            {
                Console.WriteLine("Choose a option");
                Console.WriteLine("1 Scatter Gather Expect 2 replies");
                Console.WriteLine("2 Scatter Gather unknown number of replies");

                var result = Console.ReadLine();
                switch (result)
                {
                    case "1":
                        ScatterGatherKnown();
                        break;
                    case "2":
                        ScatterGatherUnknown();
                        break;
                }
            }
        }

        private static void ScatterGatherUnknown()
        {
            var id = Guid.NewGuid();
            var responses = _bus.PublishRequest<Request, Response>(new Request(id){ Delay = true }, timeout: 500);

            foreach (var response in responses)
            {
                Console.WriteLine("Received response from - {0}", response.Endpoint);
            }

            Console.WriteLine("");
        }

        private static void ScatterGatherKnown()
        {
            var id = Guid.NewGuid();
            var responses = _bus.PublishRequest<Request, Response>(new Request(id), 2);
            foreach (var response in responses)
            {
                Console.WriteLine("Received response from - {0}", response.Endpoint);
            }

            Console.WriteLine("");
        }
    }
}
