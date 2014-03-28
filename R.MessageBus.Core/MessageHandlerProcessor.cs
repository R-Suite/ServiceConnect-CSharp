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

        public MessageHandlerProcessor(IBusContainer container)
        {
            _container = container;
        }

        public void ProcessMessage<T>(T message) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.BaseType == null || 
                                                                                    h.HandlerType.BaseType.Name != typeof(ProcessManager<>).Name);

            foreach (HandlerReference handlerReference in handlerReferences)
            {
                ExecuteHandler(message, handlerReference.HandlerType);
            }
        }

        private void ExecuteHandler<T>(T message, Type handlerType) where T : Message
        {
            try
            {
                var handler = (IMessageHandler<T>)_container.GetInstance(handlerType);
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