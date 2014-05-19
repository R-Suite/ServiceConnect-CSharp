using System;

namespace R.MessageBus.Interfaces
{
    public interface IBus : IDisposable
    {   
        /// <summary>
        /// Contains the Bus configuration.
        /// </summary>
        IConfiguration Configuration { get; set; }

        /// <summary>
        /// Sets up the Bus to start consuming messages on the given queue.
        /// </summary>
        void StartConsuming();

        /// <summary>
        /// Stop consuming messages.
        /// </summary>
        void StopConsuming();

        /// <summary>
        /// Publish message.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        void Publish<T>(T message) where T : Message;

        /// <summary>
        /// Sends a command.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        void Send<T>(T message) where T : Message;

        /// <summary>
        /// Send a command to the specified endpoint.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="endPoint"></param>
        /// <param name="message"></param>
        void Send<T>(string endPoint, T message) where T : Message;

        /// <summary>
        /// Send a command to the specified endpoint and wait for reply.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="endPoint"></param>
        /// <param name="message"></param>
        /// <param name="configureCallback"></param>
        void SendRequest<T>(string endPoint, T message, Action<IRequestConfiguration> configureCallback) where T : Message;
        
        /// <summary>
        /// Sends a command and waits for a reply.  The method behaves like a regular blocking RPC method.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="message">The message to send.</param>
        /// <returns>Returns the response object.</returns>
        TReply SendRequest<TRequest, TReply>(TRequest message) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a command to the specified endpoint and waits for a reply.  The method behaves like a regular blocking RPC method.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoint">The endpoint the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <returns>Returns the response object.</returns>
        TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message) where TRequest : Message where TReply : Message;
    }
}