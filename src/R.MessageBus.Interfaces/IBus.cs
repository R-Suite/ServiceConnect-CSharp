using System;
using System.Collections.Generic;

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
        /// <param name="headers">Custom headers</param>
        void Publish<T>(T message, Dictionary<string, string> headers) where T : Message;
        void Publish<T>(T message) where T : Message;

        /// <summary>
        /// Sends a command.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <param name="headers">Custom headers</param>
        void Send<T>(T message, Dictionary<string, string> headers) where T : Message;
        void Send<T>(T message) where T : Message;

        /// <summary>
        /// Send a command to the specified endpoint.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="endPoint"></param>
        /// <param name="message"></param>
        /// <param name="headers">Custom headers</param>
        void Send<T>(string endPoint, T message, Dictionary<string, string> headers) where T : Message;
        void Send<T>(string endPoint, T message) where T : Message;

        /// <summary>
        /// Sends a command and waits for a reply.  The method behaves like a regular blocking RPC method.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="timeout"></param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response object.</returns>
        TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers, int timeout = 3000) where TRequest : Message where TReply : Message;
        TReply SendRequest<TRequest, TReply>(TRequest message, int timeout = 3000) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a command to the specified endpoint and waits for a reply.  The method behaves like a regular blocking RPC method.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoint">The endpoint the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="timeout"></param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response object.</returns>
        TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers, int timeout = 3000) where TRequest : Message where TReply : Message;
        TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, int timeout = 3000) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a commands to the specified endpoint.  The callback is called when receving the reply message.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoint">The endpoint the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="callback">The callback that will receive the response message.</param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response object.</returns>
        void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message;
        void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a command. The callback is called when receving the reply message. 
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="callback">The callback that will receive the response message.</param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response object.</returns>
        void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers ) where TRequest : Message where TReply : Message;
        void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Implementation of Routing Slip pattern. 
        /// (Sequentially) sends the <see cref="message"/> to all the endpoints specified in <see cref="destinations"/>
        /// </summary>
        /// <typeparam name="T">The type of the message</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="destinations">Endpoints that the message is routed to</param>
        void Route<T>(T message, IList<string> destinations) where T : Message;
    }
}