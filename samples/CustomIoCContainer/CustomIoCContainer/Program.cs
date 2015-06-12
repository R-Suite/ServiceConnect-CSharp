using System;
using R.MessageBus;
using R.MessageBus.Container.StructureMap;
using R.MessageBus.Core.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Interfaces.Container;
using StructureMap;

namespace CustomIoCContainer
{
    public class Program
    {
        static void Main(string[] args)
        {
            //IContainer myContainer = new Container();
            //myContainer.Configure(c => c.For<IMessageHandler<MyMessage>>().Use<MyMessageHandler>());

            //IServicesRegistrar myContainer = new ServicesContainer();
            //myContainer.RegisterFor(typeof(MyMessageHandler), typeof(IMessageHandler<MyMessage>));

            Console.WriteLine("*********** Producer / Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
                //config.InitializeContainer(myContainer);
            });

            bus.Publish(new MyMessage(Guid.NewGuid()));
        }
    }
}
