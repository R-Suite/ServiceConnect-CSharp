using System;
using PolymorphicMessages.Messages;
using R.MessageBus;

namespace PolymorphicMessages.Producer
{
    class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof (DerivedType), "PolymorphicConsumer");
                config.AddQueueMapping(typeof (BaseType), "PolymorphicConsumer");
            });

            while (true)
            {
                Console.WriteLine("");
                Console.WriteLine("Press enter to SEND");
                Console.ReadLine();

                var id1 = Guid.NewGuid();
                bus.Send(new DerivedType(id1));

                var id2 = Guid.NewGuid();
                bus.Send(new BaseType(id2));

                Console.WriteLine("Sent messages");
                Console.WriteLine("Based: {0}", id1);
                Console.WriteLine("Derived: {0}", id2);
                Console.WriteLine("");


                Console.WriteLine("");
                Console.WriteLine("Press enter to PUBLISH");
                Console.ReadLine();

                var id3 = Guid.NewGuid();
                bus.Publish(new DerivedType(id3));

                var id4 = Guid.NewGuid();
                bus.Publish(new BaseType(id4));

                Console.WriteLine("Published messages");
                Console.WriteLine("Based: {0}", id3);
                Console.WriteLine("Derived: {0}", id4);
                Console.WriteLine("");

                Console.WriteLine("");
            }
        }
    }
}
