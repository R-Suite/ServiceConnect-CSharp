using System;
using ProcessManager.Messages;
using R.MessageBus;
using R.MessageBus.Interfaces;

namespace ProcessManager.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Client ***********");
            IBus bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
            });
            bus.StartConsuming();

            Console.WriteLine("Press <ENTER> to start ProcessManager");
            Console.ReadLine();

            bus.Send("ProcessManager.Host", new StartProcessManagerMessage(Guid.NewGuid()));
        }
    }
}
