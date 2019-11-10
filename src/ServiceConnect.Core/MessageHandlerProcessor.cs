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
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class MessageHandlerProcessor : IMessageHandlerProcessor
    {
        private readonly IBusContainer _container;
        private readonly ILogger _logger;

        public MessageHandlerProcessor(IBusContainer container, ILogger logger)
        {
            _container = container;
            _logger = logger;
        }

        public async Task ProcessMessage<T>(string message, IConsumeContext context) where T : Message
        {
            List<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>), typeof(IAsyncMessageHandler<T>))
                .Where(h => h.HandlerType.GetTypeInfo().BaseType == null || 
                            h.HandlerType.GetTypeInfo().BaseType.Name != typeof(ProcessManager<>).Name)
                .ToList();

            await InitHandlers<T>(message, context, handlerReferences);
        }

        private async Task ProcessMessageBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            List<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<TB>), typeof(IAsyncMessageHandler<TB>))
                .Where(h => h.HandlerType.GetTypeInfo().BaseType == null ||
                            h.HandlerType.GetTypeInfo().BaseType.Name != typeof(ProcessManager<>).Name)
                .ToList();
            
            await InitHandlers<T>(message, context, handlerReferences, typeof(TB));
        }        
        
        private async Task InitHandlers<T>(string message, IConsumeContext context, List<HandlerReference> handlerReferences, Type baseType = null) where T : Message
        {
            MethodInfo executeHandler = GetType().GetMethod("ExecuteHandler", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo genericexecuteHandler = null != baseType ? executeHandler.MakeGenericMethod(baseType) : executeHandler.MakeGenericMethod(typeof(T));
            
            var tasks = new List<Task>();
            foreach (HandlerReference handlerReference in handlerReferences)
            {
                object messageObject = JsonConvert.DeserializeObject(message, typeof (T));
                var result = genericexecuteHandler.Invoke(this, new[] { messageObject, handlerReference.HandlerType, handlerReference.RoutingKeys, context });
                if (result != null)
                {
                    tasks.Add((Task)result);
                }
            }

            string messageType = string.Empty;
            if (null != context && null != context.Headers && context.Headers.ContainsKey("MessageType"))
            {
                if (context.Headers["MessageType"].GetType() == typeof(string))
                {
                    messageType = (string)context.Headers["MessageType"];
                } else
                {
                    messageType = Encoding.UTF8.GetString((byte[])context.Headers["MessageType"]);
                }
            }

            // If the message was published (rather than sent), no need to scan for handlers interested in the BaseType messages...
            // The Publisher (owner of the contract) will explicitely publish all of the message's base types (if any).
            if (messageType != "Publish")
            {
                // Get message BaseType and call ProcessMessage recursively to see if there are any handlers interested in the BaseType
                Type newBaseType = (null != baseType) ? baseType.GetTypeInfo().BaseType : typeof (T).GetTypeInfo().BaseType;
                if (newBaseType != null && newBaseType.Name != typeof (object).Name)
                {
                    MethodInfo processMessage = GetType().GetMethod("ProcessMessageBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo genericProcessMessage = processMessage.MakeGenericMethod(typeof (T), newBaseType);
                    var resultTask = (Task)genericProcessMessage.Invoke(this, new object[] {message, context});
                    tasks.Add(resultTask);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task ExecuteHandler<T>(T message, Type handlerType, IList<string> routingKeys, IConsumeContext context) where T : Message
        {
            // Ignore irelevant handlers
            if (null != context && null != context.Headers && context.Headers.ContainsKey("RoutingKey"))
            {
                string msgRoutingKey;
                if (context.Headers["RoutingKey"].GetType() == typeof(string))
                {
                    msgRoutingKey = (string)context.Headers["RoutingKey"];
                }
                else
                {
                    msgRoutingKey = Encoding.UTF8.GetString((byte[])context.Headers["RoutingKey"]);
                }
                
                if (!routingKeys.Contains(msgRoutingKey) && !routingKeys.Contains("#"))
                {
                    _logger.Debug("Ignoring handler execution.");
                    return;
                }
            }
            else
            {
                if (null != routingKeys && routingKeys.Any())
                {
                    _logger.Debug("Ignoring handler execution.");
                    return;
                }
            }
                
            // Execute handler
            try
            {
                var handler = _container.GetInstance(handlerType);

                if (handler is IMessageHandler<T> syncHandler)
                {
                    syncHandler.Context = context;
                    syncHandler.Execute(message);
                }

                if (handler is IAsyncMessageHandler<T> asyncHandler)
                {
                    asyncHandler.Context = context;
                    await asyncHandler.Execute(message);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Error executing handler. {0}", handlerType.FullName), ex);
                throw;
            }
        }
    }
}