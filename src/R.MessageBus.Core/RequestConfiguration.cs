using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class RequestConfiguration : IRequestConfiguration
    {
        private readonly Guid _requestMessageId;
        private Task _task;
        private Action<object> _action;

        public Guid RequestMessageId
        {
            get { return _requestMessageId; }
        }

        public RequestConfiguration(Guid requestMessageId)
        {
            _requestMessageId = requestMessageId;
        }

        public Task SetHandler(Action<object> handler)
        {
            _task = new Task(() => {});
            _action = handler;

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
