using System;

namespace R.MessageBus.Interfaces
{
    public interface IInlineRequestConfiguration
    {
        /// <summary>
        /// Configures a handler to receive the specified type.
        /// </summary>
        /// <typeparam name="TResponse">The message type of the response</typeparam>
        /// <param name="handler">The handler to call with the response message</param>
        void Handle<TResponse>(Action<TResponse> handler)
            where TResponse : class;
    }
}
