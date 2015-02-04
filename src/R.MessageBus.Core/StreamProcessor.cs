using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class StreamProcessor : IStreamProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IBusContainer _container;

        public StreamProcessor(IBusContainer container)
        {
            _container = container;
        }

        public void ProcessMessage<T>(T message, IMessageBusReadStream stream) where T : Message
        {
            IList<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IStreamHandler<T>)).ToList();
            foreach (HandlerReference handlerReference in handlerReferences)
            {
                try
                {
                    var handler = (IStreamHandler<T>)_container.GetInstance(handlerReference.HandlerType);
                    handler.Stream = stream;
                    new Task(() => handler.Execute(message)).Start();
                }
                catch (Exception)
                {
                    Logger.Error("Error executing handler");
                    throw;
                }
            }
            stream.HandlerCount = handlerReferences.Count();
        }
    }
}
