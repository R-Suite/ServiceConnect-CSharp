using System;
using CrossVersionSupport.Messages;
using R.MessageBus;

namespace CrossVersionSupport.Producer.RMessageBus
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** CrossVersionSupport.Producer.RMessageBus ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("CrossVersionSupport.Producer.RMessageBus");
            });

            Console.WriteLine("Press enter to send messages");
            Console.ReadLine();

            var id = Guid.NewGuid();
            bus.Send("CrossVersionSupport.Consumer", new RMessageBusMessage(id) { Test = "Test RMessageBus" });
        }
    }
}
