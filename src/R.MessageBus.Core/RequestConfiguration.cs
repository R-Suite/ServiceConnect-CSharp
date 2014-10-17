using System;
using System.Threading.Tasks;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class RequestConfiguration : IRequestConfiguration
    {
        private readonly IConfiguration _configuration;
        private readonly ConsumerEventHandler _consumerEventHandler;
        private readonly Guid _correlationId;
        private readonly Guid _requestMessageId;
        private Task _task;
        private IConsumer _consumer;
        private Action<object> _action;

        public Guid RequestMessageId
        {
            get { return _requestMessageId; }
        }

        public RequestConfiguration(IConfiguration configuration, ConsumerEventHandler consumerEventHandler,  Guid correlationId, Guid requestMessageId)
        {
            _configuration = configuration;
            _consumerEventHandler = consumerEventHandler;
            _correlationId = correlationId;
            _requestMessageId = requestMessageId;
        }


        public Task SetHandler(Action<object> handler)
        {
            _consumer = _configuration.GetConsumer();
            _task = new Task(() => _consumer.Dispose());
            _action = handler;
            _consumer.StartConsuming(_consumerEventHandler, _correlationId.ToString(), _correlationId.ToString(), true, true);

            return _task;
        }

        public void ProcessMessage(object message)
        {
            _action(message);
            _task.Start();
        }
    }
}
