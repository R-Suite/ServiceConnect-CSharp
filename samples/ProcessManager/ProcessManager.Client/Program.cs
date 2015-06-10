using System;
using System.Collections.Generic;
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
            });

            Console.WriteLine("Press <ENTER> to start ProcessManager(s)");
            Console.ReadLine();

            for (int i = 1; i <= 1; i++)
            {
                bus.Send("ProcessManager.Host", new StartProcessManagerMessage(Guid.NewGuid()) { ProcessId = i});
            }
        }
    }
}
