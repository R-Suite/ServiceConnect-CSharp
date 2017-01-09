//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
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
        void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message;

        /// <summary>
        /// Publish message with a routing key.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <param name="routingKey"></param>
        /// <param name="headers">Custom headers</param>
        void Publish<T>(T message, string routingKey, Dictionary<string, string> headers = null) where T : Message;

        /// <summary>
        /// Publishes an event and wait for replies.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message</typeparam>
        /// <param name="message">The message to send</param>
        /// <param name="expectedCount">Expected number of replies. If -1 then the request will only return once the timeout has occurred</param>
        /// <param name="headers">Custom headers</param>
        /// <param name="timeout"></param>
        /// <returns>Returns a list of response objects</returns>
        IList<TReply> PublishRequest<TRequest, TReply>(TRequest message, int? expectedCount = null, Dictionary<string, string> headers = null, int timeout = 10000) where TRequest : Message;

        /// <summary>
        /// Sends a command.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <param name="headers">Custom headers</param>
        void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message;

        /// <summary>
        /// Send a command to the specified endpoint.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="endPoint"></param>
        /// <param name="message"></param>
        /// <param name="headers">Custom headers</param>
        void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message;

        /// <summary>
        ///Send a command to the specified endpoints. 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="endPoints"></param>
        /// <param name="message"></param>
        /// <param name="headers">Custom headers</param>
        void Send<T>(IList<string> endPoints, T message, Dictionary<string, string> headers = null) where T : Message;

        /// <summary>
        /// Sends a command and waits for a reply.  The method behaves like a regular blocking RPC method.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="timeout"></param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response object.</returns>
        TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers = null, int timeout = 3000) where TRequest : Message where TReply : Message;

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
        TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers = null, int timeout = 3000) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Send a command to the specified endpoints and waits for all endpoints to reply. If all the endpoints dont respond in before the timeout then responses received are returned.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoints">The endpoints the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="timeout"></param>
        /// <param name="headers">Custom headers</param>
        /// <returns>Returns the response objects.</returns>
        IList<TReply> SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Dictionary<string, string> headers = null, int timeout = 10000) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a commands to the specified endpoint.  The callback is called when receving the reply message.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoint">The endpoint the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="callback">The callback that will receive the response message.</param>
        /// <param name="headers">Custom headers</param>
        void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a command. The callback is called when receving the reply message. 
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="callback">The callback that will receive the response message.</param>
        /// <param name="headers">Custom headers</param>
        void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Sends a commands to the specified endpoint.  The callback is called when receving the reply message.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object.  Must be a message.</typeparam>
        /// <typeparam name="TReply">The type of the reply object. Must be a message.</typeparam>
        /// <param name="endPoints">The endpoints the message will be sent to.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="callback">The callback that will receive the response messages.</param>
        /// <param name="headers">Custom headers</param>
        void SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Action<IList<TReply>> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message;

        /// <summary>
        /// Implementation of Routing Slip pattern. 
        /// (Sequentially) sends the <see cref="message"/> to all the endpoints specified in <see cref="destinations"/>
        /// </summary>
        /// <typeparam name="T">The type of the message</typeparam>
        /// <param name="message">The message to send.</param>
        /// <param name="destinations">Endpoints that the message is routed to</param>
        void Route<T>(T message, IList<string> destinations) where T : Message;

        /// <summary>
        /// Creates a new stream object, which can be used to transfer large amounds of data.  Method call will establish a connection with the remote endpoint before returning.
        /// </summary>
        /// <typeparam name="T">Message Type</typeparam>
        /// <param name="endpoint">Endpoint that consume the stream</param>
        /// <param name="message">The start message to send</param>
        /// <returns>Stream for writing data</returns>
        IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    }
}