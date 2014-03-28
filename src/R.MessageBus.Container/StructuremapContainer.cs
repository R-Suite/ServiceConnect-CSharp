using System;
using System.Collections.Generic;
using System.Linq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using StructureMap;
using StructureMap.Pipeline;
using StructureMap.Query;

namespace R.MessageBus.Container
{
    public class StructuremapContainer : IBusContainer
    {
        public void Initialize()
        {
            ObjectFactory.Configure(x =>
            {
                x.For<IMessageHandlerProcessor>().Use<MessageHandlerProcessor>();
                x.For<IProcessManagerProcessor>().Use<ProcessManagerProcessor>();
            });
        }

        public void AddBus(IBus bus)
        {
            ObjectFactory.Configure(x => x.For<IBus>().Use(bus));
        }

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            IEnumerable<InstanceRef> instances = ObjectFactory.Container.Model.AllInstances.Where(i => i.PluginType.Name == typeof(IMessageHandler<>).Name ||
                                                                                                       i.PluginType.Name == typeof(IStartProcessManager<>).Name);
            return instances.Where(instance => instance.ConcreteType != null && !string.IsNullOrEmpty(instance.ConcreteType.Name))
                            .Select(instance => new HandlerReference
            {
                MessageType = instance.PluginType.GetGenericArguments()[0],
                HandlerType = instance.ConcreteType
            });
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
        {
            return ObjectFactory.Container.Model.AllInstances.Where(i => i.PluginType == messageHandler).Select(instance => new HandlerReference
            {
                MessageType = instance.PluginType.GetGenericArguments()[0],
                HandlerType = instance.ConcreteType
            });
        }

        public object GetInstance(Type handlerType)
        {
            return ObjectFactory.GetInstance(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        { 
            return ObjectFactory.GetInstance<T>(new ExplicitArguments(arguments));
        }

        public T GetInstance<T>()
        {
            return ObjectFactory.GetInstance<T>();
        }

        public void ScanForHandlers()
        {
            ObjectFactory.Configure(x => x.Scan(y =>
            {
                y.AssembliesFromApplicationBaseDirectory();
                y.ConnectImplementationsToTypesClosing(typeof(IMessageHandler<>));
            }));
        }
    }
}