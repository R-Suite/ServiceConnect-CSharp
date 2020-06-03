using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.InMemory;

namespace ServiceConnect.Container.ServiceCollection
{
    /// <summary>
    /// ServiceConnect abstraction of Microsoft.Extensions.DependencyInjection.IServiceCollection container
    /// </summary>
    public class ServiceCollectionContainer : IBusContainer
    {
        private IServiceCollection _serviceCollection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        private bool _initialized;

        public void Initialize()
        {
            if (!_initialized)
            {
                _serviceCollection.AddSingleton<IBusContainer>(this);
                _serviceCollection.AddTransient<IMessageHandlerProcessor, MessageHandlerProcessor>();
                _serviceCollection.AddTransient<IStreamProcessor, StreamProcessor>();
                _serviceCollection.AddTransient<IProcessManagerPropertyMapper, ProcessManagerPropertyMapper>();
                _serviceCollection.AddTransient<IProcessManagerProcessor, ProcessManagerProcessor>();
                _serviceCollection.AddSingleton<ILogger, Logger>();

                _serviceCollection.AddTransient<IAggregatorProcessor>(x => new AggregatorProcessor(
                   new InMemoryAggregatorPersistor("", "", ""),
                   this,
                   null,
                   new Logger()));

                // An implementation for service type IProcessManagerFinder is required for the DI software design pattern
                // However the implementation will be overriden by default or can be set via the ServiceConnect Config 
                // i.e SetProcessManagerFinder, PersistenceStoreConnectionString, PersistenceStoreDatabaseName 
                _serviceCollection.AddTransient<IProcessManagerFinder>(x => new InMemoryProcessManagerFinder("", ""));
                
                _initialized = true;
            }
        }

        public void Initialize(object container)
        {
            _serviceCollection = (IServiceCollection)container;
            
            _initialized = false;
            Initialize();
        }

        public object GetContainer()
        {
            return _serviceCollection;
        }

        public void AddHandler<T>(Type handlerType, T handler)
        {
            _serviceCollection.AddSingleton(handlerType, handler);
        }

        public void AddBus(IBus bus)
        {
            _serviceCollection.AddSingleton(x => bus);
        }

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            var instances = _serviceCollection.Where(x => x.ServiceType.Name == typeof(IMessageHandler<>).Name
                || x.ServiceType.Name == typeof(IStartProcessManager<>).Name
                || x.ServiceType.Name == typeof(IAsyncMessageHandler<>).Name
                || x.ServiceType.Name == typeof(IStartAsyncProcessManager<>).Name
                || x.ServiceType.Name == typeof(Aggregator<>).Name);

            var retval = new List<HandlerReference>();
            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.ImplementationInstance.GetType().GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.ServiceType.GetGenericArguments()[0],
                    HandlerType = instance.ImplementationInstance.GetType(),
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(params Type[] messageHandlers)
        {
            var instances = _serviceCollection.Where(i => messageHandlers.Contains(i.ServiceType));

            var retval = new List<HandlerReference>();

            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.ImplementationInstance.GetType().GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.ServiceType.GetGenericArguments()[0],
                    HandlerType = instance.ImplementationInstance.GetType(),
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        public object GetInstance(Type handlerType)
        {
            if (_serviceCollection.Any(v => v.ImplementationType == handlerType))
            {
                var serviceDescriptor = _serviceCollection.First(s => s.ImplementationType == handlerType);
                return _serviceCollection.BuildServiceProvider().GetRequiredService(serviceDescriptor.ServiceType);
            }

            if (_serviceCollection.Any(k => k.ServiceType == handlerType))
            {
                return _serviceCollection.BuildServiceProvider().GetRequiredService(handlerType);
            }

            try
            {
                var genericDefinition = handlerType.GetGenericTypeDefinition();
                if (genericDefinition != null && _serviceCollection.Any(v => v.ServiceType == genericDefinition))
                {
                    return GetGenericInstance(handlerType, _serviceCollection.First(s => s.ServiceType == genericDefinition).ServiceType);
                }
            }
            catch
            {
                return Activator.CreateInstance(handlerType);
            }

            return Activator.CreateInstance(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            if (_serviceCollection.Any(v => v.ServiceType == typeof(T)))
            {
                ConstructorInfo ctor = _serviceCollection.First(s => s.ServiceType == typeof(T)).ImplementationInstance.GetType().GetTypeInfo().GetConstructors().First();

                IList<object> dependecies = new List<object>();
                ParameterInfo[] ctorParams = ctor.GetParameters();
                foreach (ParameterInfo parameterInfo in ctorParams)
                {
                    if (arguments.ContainsKey(parameterInfo.Name))
                        dependecies.Add(arguments[parameterInfo.Name]);
                }

                var result =  ctor.Invoke(dependecies.ToArray());
                return (T)result;
            }

            throw new Exception("Type not registered" + typeof(T));
        }

        public T GetInstance<T>()
        {
            throw new NotImplementedException();
        }

        public void ScanForHandlers()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var pluginTypes = asm != null ? asm.GetTypes().Where(IsHandler).ToList() : null;

                    if (null != pluginTypes && pluginTypes.Count > 0)
                    {
                        foreach (var impl in pluginTypes)
                        {
                            var types = impl.GetTypeInfo().GetInterfaces().ToList();
                            if (impl.GetTypeInfo().BaseType != null && impl.GetTypeInfo().BaseType != typeof(object))
                            {
                                types.Add(impl.GetTypeInfo().BaseType);
                            }
                            RegisterFor(impl, types);
                        }
                    }
                }
                catch (Exception e)
                {
                }
            }
        }

        private void RegisterFor(Type implementation, IEnumerable<Type> interfaces)
        {
            foreach (var @interface in interfaces)
            {
                var sd = new ServiceDescriptor(GetRegistrableType(@interface), implementation, ServiceLifetime.Transient);
                _serviceCollection.Replace(sd);
            }
        }

        private static Type GetRegistrableType(Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetTypeInfo().ContainsGenericParameters
                ? type.GetGenericTypeDefinition()
                : type;
        }

        private static bool IsHandler(Type t)
        {
            if (t == null)
                return false;

            var isHandler = t.GetInterfaces().Any(i => i.Name == typeof(IMessageHandler<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IStartProcessManager<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IAsyncMessageHandler<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IStartAsyncProcessManager<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IStreamHandler<>).Name) ||
                            (t.GetTypeInfo().BaseType != null && t.GetTypeInfo().BaseType.Name == typeof(Aggregator<>).Name);

            return isHandler;
        }

        private object GetGenericInstance(Type tService, Type genericDefinition)
        {
            var genericArguments = tService.GetTypeInfo().GetGenericArguments();
            var actualType = genericDefinition.MakeGenericType(genericArguments);
            var result = CreateInstance(actualType);

            _serviceCollection.Add(new ServiceDescriptor(tService, result));

            return result;
        }

        private object CreateInstance(Type serviceType)
        {
            var ctor = serviceType.GetTypeInfo().GetConstructors().First();
            var dependecies = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();

            return ctor.Invoke(dependecies);
        }

        private object Resolve(Type tService)
        {
            var result = GetInstance(tService);

            return result;
        }
    }
}