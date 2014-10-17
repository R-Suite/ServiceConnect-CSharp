using System;
using System.Threading.Tasks;

namespace R.MessageBus.Interfaces
{
    public interface IRequestConfiguration
    {
        /// <summary>
        /// Configures a handler.
        /// </summary>
        /// <param name="handler">The handler to call with the response message</param>
        Task SetHandler(Action<object> handler);

        void ProcessMessage(string message, string type);
    }
}
