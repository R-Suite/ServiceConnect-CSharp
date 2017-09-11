using System;
using System.Collections.Generic;
using System.Linq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using StructureMap;
using StructureMap.Graph;
using StructureMap.Pipeline;
using StructureMap.Query;
using System.Reflection;

namespace ServiceConnect.Container.StructureMap
{
    /// <summary>
    /// ServiceConnect abstraction of StructureMap container
    /// </summary>
    public class StructureMapContainer : IBusContainer
    {
        private IContainer _container = new global::StructureMap.Container();
        private bool _initialized;

        public void Initialize()
        {
            if (!_initialized)
            {
                _container.Configure(x =>
                {
                    x.For<IMessageHandlerProcessor>().Use<MessageHandlerProcessor>();
                    x.For<IAggregatorProcessor>().Use<AggregatorProcessor>();
                    x.For<IProcessManagerProcessor>().Use<ProcessManagerProcessor>();
                    x.For<IStreamProcessor>().Use<StreamProcessor>();
                    x.For<IProcessManagerPropertyMapper>().Use<ProcessManagerPropertyMapper>();
                });

                _initialized = true;
            }
        }

        public void Initialize(object container)
        {
            _container = (IContainer) container;
            _initialized = false;
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
            IEnumerable<InstanceRef> instances = _container.Model.AllInstances.Where(
                i =>
                    i.PluginType.Name == typeof (IMessageHandler<>).Name ||
                    i.PluginType.Name == typeof (IAsyncMessageHandler<>).Name ||
                    i.PluginType.Name == typeof (IStartProcessManager<>).Name ||
                    i.PluginType.Name == typeof (IStartAsyncProcessManager<>).Name ||
                    i.PluginType.Name == typeof (Aggregator<>).Name);

            var retval = new List<HandlerReference>();
            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.ReturnedType.GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.PluginType.GetGenericArguments()[0],
                    HandlerType = instance.ReturnedType,
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(params Type[] messageHandlers)
        {
            IEnumerable<InstanceRef> instances = _container.Model.AllInstances.Where(i => messageHandlers.Contains(i.PluginType));

            var retval = new List<HandlerReference>();

            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.ReturnedType.GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.PluginType.GetTypeInfo().GetGenericArguments()[0],
                    HandlerType = instance.ReturnedType,
                    RoutingKeys = routingKeys
                });
            }

            return retval;
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
                y.AssembliesAndExecutablesFromApplicationBaseDirectory();
                y.ConnectImplementationsToTypesClosing(typeof(IMessageHandler<>));
                y.ConnectImplementationsToTypesClosing(typeof(IStartProcessManager<>));
                y.ConnectImplementationsToTypesClosing(typeof(IAsyncMessageHandler<>));
                y.ConnectImplementationsToTypesClosing(typeof(IStartAsyncProcessManager<>));
                y.ConnectImplementationsToTypesClosing(typeof(IStreamHandler<>));
                y.ConnectImplementationsToTypesClosing(typeof(Aggregator<>));
            }));
        }
    }
}
