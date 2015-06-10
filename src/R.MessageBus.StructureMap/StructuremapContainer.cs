using System;
using System.Collections.Generic;
using System.Linq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using StructureMap;
using StructureMap.Pipeline;
using StructureMap.Query;

namespace R.MessageBus.StructureMap
{
    public class StructureMapContainer : IBusContainer
    {
        private IContainer _container = ObjectFactory.Container;

        public void Initialize()
        {
            _container.Configure(x =>
            {
                x.For<IMessageHandlerProcessor>().Use<MessageHandlerProcessor>();
                x.For<IAggregatorProcessor>().Use<AggregatorProcessor>();
                x.For<IProcessManagerProcessor>().Use<ProcessManagerProcessor>();
                x.For<IStreamProcessor>().Use<StreamProcessor>();
                x.For<IProcessManagerPropertyMapper>().Use<ProcessManagerPropertyMapper>();
            });
        }

        public void Initialize(object container)
        {
            _container = (IContainer) container;
            Initialize();
        }

        public object GetContainer()
        {
            return _container;
        }

        public void AddHandler<T>(Type handlerType, T handler)
        {
            _container.Configure(x => x.For(handlerType).Singleton().Use(handler));
        }

        public void AddBus(IBus bus)
        {
            _container.Configure(x => x.For<IBus>().Singleton().Use(bus));
        }

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            IEnumerable<InstanceRef> instances = _container.Model.AllInstances.Where(i => i.PluginType.Name == typeof(IMessageHandler<>).Name ||
                                                                                                       i.PluginType.Name == typeof(IStartProcessManager<>).Name ||
                                                                                                       i.PluginType.Name == typeof(Aggregator<>).Name);
            return instances.Where(instance => instance.ConcreteType != null && !string.IsNullOrEmpty(instance.ConcreteType.Name))
                            .Select(instance => new HandlerReference
                            {
                                MessageType = instance.PluginType.GetGenericArguments()[0],
                                HandlerType = instance.ConcreteType
                            });
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
        {
            var handlers = _container.Model.AllInstances.Where(i => i.PluginType == messageHandler).Select(instance => new HandlerReference
            {
                MessageType = instance.PluginType.GetGenericArguments()[0],
                HandlerType = instance.ConcreteType
            });
            return handlers;
        }

        public object GetInstance(Type handlerType)
        {
            return _container.GetInstance(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            return _container.GetInstance<T>(new ExplicitArguments(arguments));
        }

        public T GetInstance<T>()
        {
            return _container.GetInstance<T>();
        }

        public void ScanForHandlers()
        {
            _container.Configure(x => x.Scan(y =>
            {
                y.AssembliesFromApplicationBaseDirectory();
                y.ConnectImplementationsToTypesClosing(typeof(IMessageHandler<>));
                y.ConnectImplementationsToTypesClosing(typeof(IStartProcessManager<>));
                y.ConnectImplementationsToTypesClosing(typeof(IStreamHandler<>));
                y.ConnectImplementationsToTypesClosing(typeof(Aggregator<>));
            }));
        }
    }
}
