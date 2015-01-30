using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class MessageHandlerProcessor : IMessageHandlerProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IBusContainer _container;

        public MessageHandlerProcessor(IBusContainer container)
        {
            _container = container;
        }

        public void ProcessMessage<T>(string message, IConsumeContext context) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.BaseType == null || 
                                                                                    h.HandlerType.BaseType.Name != typeof(ProcessManager<>).Name);

            InitHandlers<T>(message, context, handlerReferences);
        }

        private void ProcessMessageBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<TB>))
                                                                        .Where(h => h.HandlerType.BaseType == null ||
                                                                                    h.HandlerType.BaseType.Name != typeof(ProcessManager<>).Name);

            InitHandlers<T>(message, context, handlerReferences, typeof(TB));
        }        
        
        private void InitHandlers<T>(string message, IConsumeContext context, IEnumerable<HandlerReference> handlerReferences, Type baseType = null) where T : Message
        {
            string executeHandlerMethodName = (null != baseType) ? "ExecuteHandlerBaseType" : "ExecuteHandler";
            MethodInfo executeHandler = GetType().GetMethod(executeHandlerMethodName, BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo genericexecuteHandler = (null != baseType) ? executeHandler.MakeGenericMethod(typeof(T), baseType) : executeHandler.MakeGenericMethod(typeof(T));

            foreach (HandlerReference handlerReference in handlerReferences)
            {
                object messageObject = JsonConvert.DeserializeObject(message, typeof (T));
                genericexecuteHandler.Invoke(this, new[] {messageObject, handlerReference.HandlerType, context});
            }

            // This is used when processing Sent (rather than Published) messages
            // Get message BaseType and call ProcessMessage recursively to see if there are any handlers interested in the BaseType
            Type newBaseType = (null != baseType) ? baseType.BaseType : typeof(T).BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(object).Name)
            {
                MethodInfo processMessage = GetType().GetMethod("ProcessMessageBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericProcessMessage = processMessage.MakeGenericMethod(typeof(T), newBaseType);
                genericProcessMessage.Invoke(this, new object[] {message, context});
            }
        }

        private void ExecuteHandlerBaseType<T, TB>(T message, Type handlerType, IConsumeContext context) where TB : Message where T : TB
        {
            try
            {
                var handler = (IMessageHandler<TB>)_container.GetInstance(handlerType);
                handler.Context = context;
                handler.Execute(message);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Error executing handler. {0}", handlerType.FullName), ex);
                throw;
            }
        }

        private void ExecuteHandler<T>(T message, Type handlerType, IConsumeContext context) where T : Message
        {
            try
            {
                var handler = (IMessageHandler<T>)_container.GetInstance(handlerType);
                handler.Context = context;
                handler.Execute(message);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Error executing handler. {0}", handlerType.FullName), ex);
                throw;
            }
        }
    }
}