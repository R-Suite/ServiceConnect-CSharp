using System;
using System.Threading.Tasks;

namespace R.MessageBus.Interfaces
{
    public interface IRequestConfiguration
    {
        /// <summary>
        /// Keeps track of the original request message
        /// Check this property when processing reply messages to ensure the request is not proccessed as a reply. 
        /// </summary>
        Guid RequestMessageId { get; }

        int EndpointsCount { get; set; }

        /// <summary>
        /// Configures a handler.
        /// </summary>
        /// <param name="handler">The handler to call with the response message</param>
        Task SetHandler(Action<object> handler);

        void ProcessMessage(string message, string type);
    }
}
