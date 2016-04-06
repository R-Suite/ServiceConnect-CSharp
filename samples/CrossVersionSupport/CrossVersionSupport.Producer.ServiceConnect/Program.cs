using System;
using CrossVersionSupport.Messages;
using ServiceConnect;

namespace CrossVersionSupport.Producer.ServiceConnect
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** CrossVersionSupport.Producer.ServiceConnect ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("CrossVersionSupport.Producer.ServiceConnect");
            });

            Console.WriteLine("Press enter to send messages");
            Console.ReadLine();

            var id = Guid.NewGuid();
            bus.Send("CrossVersionSupport.Consumer", new ServiceConnectMessage(id) { Test = "Test ServiceConnect" });
        }
    }
}
