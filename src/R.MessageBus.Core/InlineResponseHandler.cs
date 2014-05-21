using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class InlineResponseHandler<TResponse> : IMessageHandler<TResponse> where TResponse : Message
    {
        private Action<TResponse> _action;

        public IConsumeContext Context { get; set; }

        public void Execute(TResponse message)
        {
            _action(message);
        }

        public void SetAction(Action<TResponse> action)
        {
            _action = action;
        }
    }
}
