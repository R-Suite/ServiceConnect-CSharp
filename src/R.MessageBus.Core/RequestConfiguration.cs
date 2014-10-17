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
        private readonly IMessageSerializer _messageSerializer;
        private Task _task;
        private IConsumer _consumer;
        private Action<object> _action;

        public RequestConfiguration(IConfiguration configuration, ConsumerEventHandler consumerEventHandler,  Guid correlationId, IMessageSerializer messageSerializer)
        {
            _configuration = configuration;
            _consumerEventHandler = consumerEventHandler;
            _correlationId = correlationId;
            _messageSerializer = messageSerializer;
        }

        public Task SetHandler(Action<object> handler)
        {
            _consumer = _configuration.GetConsumer();
            _task = new Task(() => _consumer.Dispose());
            _action = handler;
            _consumer.StartConsuming(_consumerEventHandler, _correlationId.ToString(), _correlationId.ToString(), true);

            return _task;
        }

        public void ProcessMessage(string message, string type)
        {
            var messageObject = _messageSerializer.Deserialize(type, message);
            _action(messageObject);
            _task.Start();
        }
    }
}
