using System;

namespace CrossVersionSupport.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** CrossVersionSupport.Consumer.R.MessageBus ***********");
            R.MessageBus.Bus.Initialize(x =>
            {
                x.SetQueueName("CrossVersionSupport.Consumer");
            });

            Console.WriteLine("*********** CrossVersionSupport.Consumer.ServiceConnect ***********");
            ServiceConnect.Bus.Initialize(x =>
            {
                x.SetQueueName("CrossVersionSupport.Consumer");
            });

            Console.ReadLine();
        }
    }
}
