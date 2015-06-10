using System;
using R.MessageBus;
using R.MessageBus.Interfaces;
using R.MessageBus.StructureMap;
using StructureMap;

namespace CustomIoCContainer
{
    public class Program
    {
        static void Main(string[] args)
        {
            IContainer myContainer = new Container();
            myContainer.Configure(c => c.For<IMessageHandler<MyMessage>>().Use<MyMessageHandler>());

            Console.WriteLine("*********** Producer / Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = false;
                config.InitializeContainer(myContainer);
            });

            bus.Publish(new MyMessage(Guid.NewGuid()));
        }
    }
}
