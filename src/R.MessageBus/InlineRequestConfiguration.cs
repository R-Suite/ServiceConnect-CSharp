using System;
using System.Threading.Tasks;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class InlineRequestConfiguration : IInlineRequestConfiguration
    {
        private readonly IConfiguration _configuration;
        private readonly ConsumerEventHandler _messageReceived;
        private readonly Guid _correlationId;

        public InlineRequestConfiguration(IConfiguration configuration, ConsumerEventHandler messageReceived, Guid correlationId)
        {
            _configuration = configuration;
            _messageReceived = messageReceived;
            _correlationId = correlationId;
        }

        public Task SetHandler<TResponse>(Action<TResponse> handler) where TResponse : Message
        {
            // Used for synchronisation only
            var task = new Task(() => { });

            // Action will be executed in the body of the handler. 
            var action = new Action<TResponse>(r =>
            {
                handler(r);
                task.Start(); 
            });

            var inlineResposeHandler = new InlineResponseHandler<TResponse>();
            inlineResposeHandler.SetAction(action);

            //todo: create consumer and start consuming
            IConsumer consumer = _configuration.GetConsumer();
            consumer.StartConsuming(_messageReceived, _correlationId.ToString(), _correlationId.ToString()); // will need to take additional parameter to override queue durability

            return task;
        }
    }
}
