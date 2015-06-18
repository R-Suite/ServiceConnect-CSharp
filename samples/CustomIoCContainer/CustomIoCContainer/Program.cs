using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ninject;
using Ninject.Activation;
using Ninject.Components;
using Ninject.Infrastructure;
using Ninject.Planning.Bindings;
using Ninject.Planning.Bindings.Resolvers;
using R.MessageBus;
using R.MessageBus.Container.Ninject;
using R.MessageBus.Container.StructureMap;
using R.MessageBus.Core.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Interfaces.Container;
using StructureMap;
using Ninject.Extensions.Conventions;

namespace CustomIoCContainer
{
    public class MyBindingResolver : NinjectComponent, IBindingResolver
    {
        /// <summary>
        /// Returns any bindings from the specified collection that match the specified service.
        /// </summary>
        public IEnumerable<IBinding> Resolve(Multimap<Type, IBinding> bindings, Type service)
        {
            if (service.IsGenericType)
            {
                var genericType = service.GetGenericTypeDefinition();
                //var genericArguments = service.GetGenericArguments();
                if (service.IsGenericTypeDefinition)
                {
                    //var argument = service.GetGenericArguments().Single();
                    return bindings.Where(kvp => kvp.Key.IsGenericType
                                              && kvp.Key.GetGenericTypeDefinition() == genericType)
                               .SelectMany(kvp => kvp.Value);
                }
            }

            return Enumerable.Empty<IBinding>();
        }
    }

    public class Program
    {
        static void Main(string[] args)
        {
            //IContainer myContainer = new Container();
            //myContainer.Configure(c => c.For<IMessageHandler<MyMessage>>().Use<MyMessageHandler>());

            //IServicesRegistrar myContainer = new ServicesContainer();
            //myContainer.RegisterFor(typeof(MyMessageHandler), typeof(IMessageHandler<MyMessage>));

            var myContainer = new StandardKernel();
            myContainer.Bind(typeof(IMessageHandler<MyMessage>)).To(typeof(MyMessageHandler));

            //myContainer.GetBindings(typeof(IMessageHandler))

            //var test = myContainer.Get(typeof(IMessageHandler<>), i => i.Name.StartsWith("IMessageHandler"));

            //myContainer.Components.Add<IBindingResolver, MyBindingResolver>();

            //var test4 = myContainer.GetAll(typeof(IMessageHandler<>));
            //var test5 = myContainer.GetAll(typeof(IMessageHandler<MyMessage>));

            Console.WriteLine("*********** Producer / Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
                config.SetContainerType<NinjectContainer>();
                //config.InitializeContainer(myContainer);
            });

            bus.Configuration.InitializeContainer(myContainer);

            bus.Publish(new MyMessage(Guid.NewGuid()));
        }
    }
}
