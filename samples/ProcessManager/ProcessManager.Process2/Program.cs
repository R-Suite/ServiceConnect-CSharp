using System;
using System.Collections.Generic;
using ProcessManager.Messages;
using ServiceConnect;
using ServiceConnect.Interfaces;

namespace ProcessManager.Process2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Process2 ***********");
            Bus.Initialize(config =>
            {
                config.SetThreads(20);
                config.SetHost("localhost");
            });
        }
    }
}
