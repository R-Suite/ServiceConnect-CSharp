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
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using StructureMap;
using StructureMap.Pipeline;
using StructureMap.Query;

namespace R.MessageBus.Container
{
    public class StructuremapContainer : IBusContainer
    {
        private IContainer _smContainer = ObjectFactory.Container;

        public void Initialize()
        {
            _smContainer.Configure(x =>
            {
                x.For<IMessageHandlerProcessor>().Use<MessageHandlerProcessor>();
                x.For<IAggregatorProcessor>().Use<AggregatorProcessor>();
                x.For<IProcessManagerProcessor>().Use<ProcessManagerProcessor>();
                x.For<IStreamProcessor>().Use<StreamProcessor>();
                x.For<IProcessManagerPropertyMapper>().Use<ProcessManagerPropertyMapper>();
            });
        }

        public void Initialize(IContainer smContainer)
        {
            _smContainer = smContainer;
            Initialize();
        }

        public void AddHandler<T>(Type handlerType, T handler)
        {
            _smContainer.Configure(x => x.For(handlerType).Singleton().Use(handler));
        }

        public void AddBus(IBus bus)
        {
            _smContainer.Configure(x => x.For<IBus>().Singleton().Use(bus));
        }

        public IEnumerable<HandlerReference> GetHandlerTypes()
        {
            IEnumerable<InstanceRef> instances = _smContainer.Model.AllInstances.Where(i => i.PluginType.Name == typeof(IMessageHandler<>).Name ||
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
            var handlers = _smContainer.Model.AllInstances.Where(i => i.PluginType == messageHandler).Select(instance => new HandlerReference
            {
                MessageType = instance.PluginType.GetGenericArguments()[0],
                HandlerType = instance.ConcreteType
            });
            return handlers;
        }

        public object GetInstance(Type handlerType)
        {
            return _smContainer.GetInstance(handlerType);
        }

        public T GetInstance<T>(IDictionary<string, object> arguments)
        {
            return _smContainer.GetInstance<T>(new ExplicitArguments(arguments));
        }

        public T GetInstance<T>()
        {
            return _smContainer.GetInstance<T>();
        }

        public void ScanForHandlers()
        {
            _smContainer.Configure(x => x.Scan(y =>
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