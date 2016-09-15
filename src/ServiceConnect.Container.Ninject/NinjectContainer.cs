using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ninject.Extensions.Conventions;
using Ninject;
using Ninject.Parameters;
using Ninject.Planning.Bindings.Resolvers;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Container.Ninject
{
    /// <summary>
    /// ServiceConnect abstraction of Ninject container
    /// </summary>
    public class NinjectContainer : IBusContainer
    {
        StandardKernel _kernel = new StandardKernel();
        private bool _initialized;

        public void Initialize()
        {
            if (!_initialized)
            {
                _kernel.Components.Add<IBindingResolver, CustomBindingResolver>();

                _kernel.Bind<IMessageHandlerProcessor>().To<MessageHandlerProcessor>();
                _kernel.Bind<IAggregatorProcessor>().To<AggregatorProcessor>();
                _kernel.Bind<IProcessManagerProcessor>().To<ProcessManagerProcessor>();
                _kernel.Bind<IStreamProcessor>().To<StreamProcessor>();
                _kernel.Bind<IProcessManagerPropertyMapper>().To<ProcessManagerPropertyMapper>();

                _initialized = true;
            }
        }

        public void Initialize(object container)
        {
            _kernel = (StandardKernel)container;
            _initialized = false;
            Initialize();
        }

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            var retval = new List<HandlerReference>();

            var handlerTypes = new List<object>();
            handlerTypes.AddRange(_kernel.GetAll(typeof(IMessageHandler<>)));
            handlerTypes.AddRange(_kernel.GetAll(typeof(IStartProcessManager<>)));
            handlerTypes.AddRange(_kernel.GetAll(typeof(Aggregator<>)));

            foreach (var handlerType in handlerTypes)
            {
                IEnumerable<object> attrs = handlerType.GetType().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                Type messageType = null;
                foreach (Type intType in handlerType.GetType().GetInterfaces())
                {
                    // In case handlers implement other interfaces
                    if (intType.IsGenericType &&
                        (intType.GetGenericTypeDefinition() == typeof(IMessageHandler<>) ||
                         intType.GetGenericTypeDefinition() == typeof(IStartProcessManager<>) ||
                         intType.GetGenericTypeDefinition() == typeof(Aggregator<>)))
                    {
                        messageType = intType.GetGenericArguments()[0];
                        break;
                    }
                }

                retval.Add(new HandlerReference
                {
                    MessageType = messageType,
                    HandlerType = handlerType.GetType(),
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
        {
            var retval = new List<HandlerReference>();

            var handlerTypes = _kernel.GetAll(messageHandler);

            foreach (var handlerType in handlerTypes)
            {
                IEnumerable<object> attrs = handlerType.GetType().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                Type messageType = null;
                foreach (Type intType in handlerType.GetType().GetInterfaces())
                {
                    // In case handlers implement other interfaces
                    if (intType.IsGenericType &&
                        (intType.GetGenericTypeDefinition() == typeof(IMessageHandler<>) ||
                         intType.GetGenericTypeDefinition() == typeof(IStartProcessManager<>) ||
                         intType.GetGenericTypeDefinition() == typeof(Aggregator<>)))
                    {
                        messageType = intType.GetGenericArguments()[0];
                        break;
                    }
                }

                retval.Add(new HandlerReference
                {
                    MessageType = messageType,
                    HandlerType = handlerType.GetType(),
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        public object GetInstance(Type handlerType)
        {
            return _kernel.Get(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            IList<IParameter> ctorArgs =
                arguments.Select(argument => new ConstructorArgument(argument.Key, argument.Value))
                    .Cast<IParameter>()
                    .ToList();
            
            return _kernel.Get<T>(ctorArgs.ToArray());
        }

        public T GetInstance<T>()
        {
            return _kernel.Get<T>();
        }

        public void ScanForHandlers()
        {
            string codeBase = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);

            _kernel.Bind(
                x =>
                    x.FromAssembliesInPath(codeBase)
                        .SelectAllClasses()
                        .InheritedFromAny(new[]
                        {
                            typeof (IMessageHandler<>),
                            typeof (IStartProcessManager<>),
                            typeof (IStreamHandler<>)
                        }).BindAllInterfaces());

            _kernel.Bind(
                x =>
                    x.FromAssembliesInPath(codeBase)
                        .SelectAllClasses().InheritedFrom(typeof (Aggregator<>))
                        .BindAllBaseClasses());
        }

        public void AddHandler<T>(Type handlerType, T handler)
        {
            _kernel.Bind(handlerType).ToConstant(handler).InSingletonScope();
        }

        public void AddBus(IBus bus)
        {
            _kernel.Bind<IBus>().ToConstant(bus).InSingletonScope();
        }

        public object GetContainer()
        {
            return _kernel;
        }
    }
}
