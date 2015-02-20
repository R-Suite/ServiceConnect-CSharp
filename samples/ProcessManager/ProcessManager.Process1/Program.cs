using System;
using R.MessageBus;
using R.MessageBus.Interfaces;

namespace ProcessManager.Process1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Process1 ***********");
            Bus.Initialize();
        }
    }
}
