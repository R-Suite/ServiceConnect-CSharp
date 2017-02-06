using System;
using System.Threading;
using ProcessManager.Messages;
using ServiceConnect;
using ServiceConnect.Interfaces;

namespace ProcessManager.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Client ***********");
            IBus bus = Bus.Initialize(config =>
            {
                config.SetHost("localhost");
            });

            Console.WriteLine("Press <ENTER> to start ProcessManager(s)");
            Console.ReadLine();

            State.Pms = 1;

            while (State.Pms != 64)
            {
                Console.WriteLine("** {0}", State.Pms);

                State.Start = DateTime.UtcNow;

                for (int i = 1; i <= State.Pms; i++)
                {
                    bus.Send("ProcessManager.Host", new StartProcessManagerMessage(Guid.NewGuid()));
                }

                while (!State.Finished)
                {
                    Thread.Sleep(500);
                }

                State.Finished = false;
                State.Pms = State.Pms * 2;
            }
            
            Console.ReadLine();
        }
    }
}
