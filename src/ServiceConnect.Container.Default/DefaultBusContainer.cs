//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Container.Default
{
    /// <summary>
    /// ServiceConnect abstraction of the custom IoC Container.
    /// Used as default to remove any hard dependencies on third-party containers.
    /// </summary>
    public class DefaultBusContainer : IBusContainer
    {
        private Container _container = new Container();

        /// <summary>
        /// Get all handler references for the current container
        /// </summary>
        /// <returns></returns>
        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            IEnumerable<KeyValuePair<ServiceDescriptor, Type>> instances = _container.AllInstances.Where(
                i =>
                    i.Value.Name == typeof (IMessageHandler<>).Name ||
                    i.Value.Name == typeof(IStartProcessManager<>).Name ||
                    i.Value.Name == typeof(Aggregator<>).Name);

            var retval = new List<HandlerReference>();
            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.Key.ServiceType.GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.Value.GetGenericArguments()[0],
                    HandlerType = instance.Key.ServiceType,
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        /// <summary>
        /// Get handler references for a handler type (e.g. IMessageHandler`1)
        /// </summary>
        /// <param name="messageHandler"></param>
        /// <returns></returns>
        public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
        {
            IEnumerable<KeyValuePair<ServiceDescriptor, Type>> instances = _container.AllInstances.Where(i => i.Value == messageHandler);

            var retval = new List<HandlerReference>();

            foreach (var instance in instances)
            {
                IEnumerable<object> attrs = instance.Key.ServiceType.GetTypeInfo().GetCustomAttributes(false);
                var routingKeys = attrs.OfType<RoutingKey>().Select(rk => rk.GetValue()).ToList();

                retval.Add(new HandlerReference
                {
                    MessageType = instance.Value.GetGenericArguments()[0],
                    HandlerType = instance.Key.ServiceType,
                    RoutingKeys = routingKeys
                });
            }

            return retval;
        }

        /// <summary>
        /// Get instance for a handler type with parameterless ctor
        /// </summary>
        /// <param name="handlerType"></param>
        /// <returns>handler instance</returns>
        public object GetInstance(Type handlerType)
        {
            return _container.Resolve(handlerType);
        }

        /// <summary>
        /// Get typed instance for a handler type with parameterless ctor
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>handler instance</returns>
        public T GetInstance<T>()
        {
            return _container.Resolve<T>();
        }

        /// <summary>
        /// Get instance for a handler type with ctor parameters
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="arguments"></param>
        /// <returns>handler instance</returns>
        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            return (T) _container.Resolve(typeof(T), arguments);
        }

        /// <summary>
        /// Scan all assemblies loaded into the current appdomain for message handlers
        /// </summary>
        public void ScanForHandlers()
        {
#if NETSTANDARD1_6
            var assemblies = Microsoft.Extensions.DependencyModel.DependencyContext.Default.RuntimeLibraries;
            foreach (var assembly in assemblies)
            {
                try 
                {          
                    var asm = Assembly.Load(new AssemblyName(assembly.Name));
                    var pluginTypes = asm != null ? asm.GetTypes().Where(IsHandler).ToList() : null;

                    if (null != pluginTypes && pluginTypes.Count > 0)
                    {
                        _container.RegisterForAll(pluginTypes);
                    }
                }
                catch (Exception)
                { }
            }
#else
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var pluginTypes = asm != null ? asm.GetTypes().Where(IsHandler).ToList() : null;

                if (null != pluginTypes && pluginTypes.Count > 0)
                {
                    _container.RegisterForAll(pluginTypes);
                }
            }
#endif
        }

        /// <summary>
        /// Register all the internal message processors with a new/empty container
        /// </summary>
        public void Initialize()
        {
            _container.RegisterForAll(typeof(MessageHandlerProcessor));
            _container.RegisterForAll(typeof(AggregatorProcessor));
            _container.RegisterForAll(typeof(ProcessManagerProcessor));
            _container.RegisterForAll(typeof(StreamProcessor));
            _container.RegisterForAll(typeof(ProcessManagerPropertyMapper));
        }

        /// <summary>
        /// Register all the internal message processors with a provided container
        /// </summary>
        /// <param name="container"></param>
        public void Initialize(object container)
        {
            _container = (Default.Container)container;
            Initialize();
        }

        /// <summary>
        /// Register instance of a handler with the current container
        /// </summary>
        /// <typeparam name="T">generic handler type</typeparam>
        /// <param name="handlerType">type of the handler instance</param>
        /// <param name="handler">handler instance</param>
        public void AddHandler<T>(Type handlerType, T handler)
        {
            _container.RegisterFor(handler, handlerType);
        }

        /// <summary>
        /// Register instance of the <see cref="IBus"/> to the current container
        /// </summary>
        /// <param name="bus"></param>
        public void AddBus(IBus bus)
        {
            _container.RegisterFor(bus, typeof(IBus));
        }

        /// <summary>
        /// Get instance of the current container
        /// </summary>
        /// <returns></returns>
        public object GetContainer()
        {
            return _container;
        }

        private static bool IsHandler(Type t)
        {
            if (t == null)
                return false;

            var isHandler = t.GetInterfaces().Any(i => i.Name == typeof(IMessageHandler<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IStartProcessManager<>).Name) ||
                            t.GetInterfaces().Any(i => i.Name == typeof(IStreamHandler<>).Name) ||
                            (t.GetTypeInfo().BaseType != null && t.GetTypeInfo().BaseType.Name == typeof(Aggregator<>).Name);

            return isHandler;
        }
    }
}
