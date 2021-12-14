using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ServiceConnect.Core
{
    public class ProcessMessagePipeline : IProcessMessagePipeline
    {
        private readonly IConfiguration _configuration;
        private readonly IBusState _busState;
        private readonly IBusContainer _container;
        private readonly ILogger _logger;

        public ProcessMessagePipeline(IConfiguration configuration, IBusState busState)
        {
            _configuration = configuration;
            _busState = busState;
            _container = configuration.GetContainer();
            _logger = configuration.GetLogger();
        }

        public async Task ExecutePipeline(IConsumeContext context, Type typeObject, Envelope envelope)
        {
            // Build process message pipeline
            ProcessMessageDelegate current = ProcessMessage;
            for (int i = _configuration.MessageProcessingMiddleware.Count; i > 0; i--)
            {
                var middleware = (IProcessMessageMiddleware)_container.GetInstance(_configuration.MessageProcessingMiddleware[i - 1]);
                middleware.Next = current;
                current = middleware.Process;
            }
            // Execute first delegate
            await current(context, typeObject, envelope);
        }

        private async Task ProcessMessage(IConsumeContext context, Type typeObject, Envelope envelope)
        {
            var tasks = new List<Task>();
            tasks.Add(ProcessMessageHandlers(envelope.Body, typeObject, context));
            tasks.Add(ProcessProcessManagerHandlers(envelope.Body, typeObject, context));
            ProcessAggregatorHandlers(envelope.Body, typeObject);
            ProcessRequestReplyConfigurations(envelope.Body, typeObject, context);
            await Task.WhenAll(tasks);
        }

        private async Task ProcessProcessManagerHandlers(byte[] objectMessage, Type type, IConsumeContext context)
        {
            IProcessManagerFinder processManagerFinder = _configuration.GetProcessManagerFinder();
            var processManagerProcessor = _container.GetInstance<IProcessManagerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"processManagerFinder", processManagerFinder},
                {"logger", _logger }
            });

            MethodInfo processManagerProcessorMethod = processManagerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(type);
            await (Task)genericProcessManagerProcessorMethod.Invoke(processManagerProcessor, new object[] { Encoding.UTF8.GetString(objectMessage), context });
        }

        private async Task ProcessMessageHandlers(byte[] objectMessage, Type type, IConsumeContext context)
        {
            var messageHandlerProcessor = _container.GetInstance<IMessageHandlerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"logger", _logger }
            });
            MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(type);
            var result = genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new object[] { Encoding.UTF8.GetString(objectMessage), context });
            await (Task)result;
        }

        private void ProcessRequestReplyConfigurations(byte[] byteMessage, Type typeObject, IConsumeContext context)
        {
            lock (_busState.RequestLock)
            {
                if (!context.Headers.ContainsKey("ResponseMessageId"))
                {
                    return;
                }

                string messageId = Encoding.UTF8.GetString((byte[])context.Headers["ResponseMessageId"]);
                if (!_busState.RequestConfigurations.ContainsKey(messageId))
                {
                    return;
                }
                IRequestConfiguration requestConfigration = _busState.RequestConfigurations[messageId];

                requestConfigration.ProcessMessage(Encoding.UTF8.GetString(byteMessage), typeObject);

                if (requestConfigration.ProcessedCount == requestConfigration.EndpointsCount)
                {
                    var item = _busState.RequestConfigurations.First(kvp => kvp.Key == messageId);
                    _busState.RequestConfigurations.Remove(item.Key);
                }
            }
        }

        private void ProcessAggregatorHandlers(byte[] objectMessage, Type type)
        {
            if (_busState.AggregatorProcessors.ContainsKey(type))
            {
                IAggregatorProcessor aggregatorProcessor = _busState.AggregatorProcessors[type];

                MethodInfo aggregatorProcessorMethod = aggregatorProcessor.GetType().GetMethod("ProcessMessage");
                MethodInfo genericAggregatorProcessorMethod = aggregatorProcessorMethod.MakeGenericMethod(type);
                genericAggregatorProcessorMethod.Invoke(aggregatorProcessor, new object[] { Encoding.UTF8.GetString(objectMessage) });
            }
        }

    }
}
