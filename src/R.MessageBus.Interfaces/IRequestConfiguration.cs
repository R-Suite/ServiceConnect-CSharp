using System;
using System.Threading.Tasks;

namespace R.MessageBus.Interfaces
{
    public interface IRequestConfiguration
    {
        /// <summary>
        /// Configures a handler to receive the specified type.
        /// </summary>
        /// <typeparam name="TResponse">The message type of the response</typeparam>
        /// <param name="handler">The handler to call with the response message</param>
        Task SetHandler<TResponse>(Action<TResponse> handler)
            where TResponse : Message;
    }
}
