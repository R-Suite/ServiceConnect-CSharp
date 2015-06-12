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
using System.IO;
using System.Linq;
using System.Reflection;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core.Container
{
    /// <summary>
    /// Abstraction of Custom IoC Container.
    /// Used as default to remove any hard dependencies on third-party containers.
    /// </summary>
    public class DefaultBusContainer : IBusContainer
    {
        private Container _container = ContainerSingleton._instance; // using field for performance

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            IEnumerable<KeyValuePair<Type, ServiceDescriptor>> instances = _container.AllInstances.Where(
                i =>
                    i.Key.Name == typeof (IMessageHandler<>).Name ||
                    i.Key.Name == typeof (IStartProcessManager<>).Name ||
                    i.Key.Name == typeof (Aggregator<>).Name);


            return instances.Where(
                instance => instance.Value.ServiceType != null && !string.IsNullOrEmpty(instance.Value.ServiceType.Name))
                .Select(instance => new HandlerReference
                {
                    MessageType = instance.Key.GetGenericArguments()[0],
                    HandlerType = instance.Value.ServiceType
                });
        }

        public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
        {
            var handlers = _container.AllInstances.Where(i => i.Key == messageHandler).Select(instance => new HandlerReference
            {
                MessageType = instance.Key.GetGenericArguments()[0],
                HandlerType = instance.Value.ServiceType
            });

            return handlers;
        }

        public object GetInstance(Type handlerType)
        {
            return _container.Resolve(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            return (T) _container.Resolve(typeof(T), arguments);
        }

        public T GetInstance<T>()
        {
            return _container.Resolve<T>();
        }

        public void ScanForHandlers()
        {
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var list = new List<string>();
            list.AddRange(Directory.GetFiles(path, "*.dll"));
            list.AddRange(Directory.GetFiles(path, "*.exe"));

            foreach (string dll in list)
            {
                if (!string.IsNullOrEmpty(dll) && File.Exists(dll))
                {
                    var asm = Assembly.LoadFrom(dll);
                    Type pluginType = asm != null ? asm.GetTypes().Where(IsHandler).FirstOrDefault() : null;

                    if (pluginType != null)
                    {

                        //if (pluginType.GetInterfaces().Any(i => i.Name == "IStartProcessManager`1"))
                        //{
                        //    _container.RegisterFor(pluginType.TypeHandle);
                        //}
                        //else
                            _container.RegisterForAll(pluginType);
                    }
                }
            }
        }

        public void Initialize()
        {
            _container.RegisterForAll(typeof(MessageHandlerProcessor));
            _container.RegisterForAll(typeof(AggregatorProcessor));
            _container.RegisterForAll(typeof(ProcessManagerProcessor));
            _container.RegisterForAll(typeof(StreamProcessor));
            _container.RegisterForAll(typeof(ProcessManagerPropertyMapper));
        }

        public void Initialize(object container)
        {
            _container = (Container)container;
            Initialize();
        }

        public void AddHandler<T>(Type handlerType, T handler)
        {
            _container.RegisterFor(handler, handlerType);
        }

        public void AddBus(IBus bus)
        {
            _container.RegisterFor(bus, typeof(IBus));
        }

        public object GetContainer()
        {
            return _container;
        }

        private static bool IsHandler(Type t)
        {
            if (t == null)
                return false;

            var isHandler = t.GetInterfaces().Any(i => i.Name == "IMessageHandler`1") ||
                            t.GetInterfaces().Any(i => i.Name == "IStartProcessManager`1") ||
                            t.GetInterfaces().Any(i => i.Name == "IStreamHandler`1") ||
                            t.BaseType == typeof(Aggregator<>);
            return isHandler;
        }
    }
}
