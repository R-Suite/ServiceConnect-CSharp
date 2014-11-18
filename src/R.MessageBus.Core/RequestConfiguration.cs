using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class RequestConfiguration : IRequestConfiguration
    {
        private readonly IConfiguration _configuration;
        private readonly ConsumerEventHandler _consumerEventHandler;
        private readonly Guid _requestMessageId;
        private readonly string _messageType;
        private Task _task;
        private IConsumer _consumer;
        private Action<object> _action;

        public Guid RequestMessageId
        {
            get { return _requestMessageId; }
        }

        public RequestConfiguration(IConfiguration configuration, ConsumerEventHandler consumerEventHandler,  Guid requestMessageId, string messageType)
        {
            _configuration = configuration;
            _consumerEventHandler = consumerEventHandler;
            _requestMessageId = requestMessageId;
            _messageType = messageType;
        }

        public Task SetHandler(Action<object> handler)
        {
            _consumer = _configuration.GetConsumer();
            _task = new Task(() => _consumer.Dispose());
            _action = handler;
            _consumer.StartConsuming(_consumerEventHandler, _messageType, _configuration.TransportSettings.Queue.Name, _configuration.TransportSettings.Queue.Exclusive, _configuration.TransportSettings.Queue.AutoDelete);

            return _task;
        }

        public void ProcessMessage(string message, string type)
        {
            var messageObject = JsonConvert.DeserializeObject(message, Type.GetType(type));
            _action(messageObject);
            _task.Start();
        }
    }
}
