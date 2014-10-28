using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using log4net;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class MessageHandlerProcessor : IMessageHandlerProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IBusContainer _container;
        private readonly IMessageSerializer _messageSerializer;

        public MessageHandlerProcessor(IBusContainer container, IMessageSerializer messageSerializer)
        {
            _container = container;
            _messageSerializer = messageSerializer;
        }

        public void ProcessMessage<T>(string message, IConsumeContext context) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.BaseType == null || 
                                                                                    h.HandlerType.BaseType.Name != typeof(ProcessManager<>).Name);

            MethodInfo executeHandler = GetType().GetMethod("ExecuteHandler", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo genericexecuteHandler = executeHandler.MakeGenericMethod(typeof(T));

            foreach (HandlerReference handlerReference in handlerReferences)
            {
                object messageObject = _messageSerializer.Deserialize(typeof (T).AssemblyQualifiedName, message);
                genericexecuteHandler.Invoke(this, new[] { messageObject, handlerReference.HandlerType, context });
            }

            // This is used when processing Sent (rather than Published) messages
            // Get message BaseType and call ProcessMessage recursively to see if there are any handlers interested in the BaseType
            Type baseType = typeof(T).BaseType;
            if (baseType != null && baseType.Name != typeof (Message).Name)
            {
                MethodInfo processMessage = GetType().GetMethod("ProcessMessage", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo genericProcessMessage = processMessage.MakeGenericMethod(baseType);
                genericProcessMessage.Invoke(this, new object[] { message, context });
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