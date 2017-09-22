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
    public interface IConfiguration
    {
        Type ConsumerType { get; set; }
        Type ProducerType { get; set; }
        Type ProcessManagerFinder { get; set; }
        Type AggregatorPersistor { get; set; }
        Type MessageBusReadStream { get; set; }
        Type MessageBusWriteStream { get; set; }
        Type AggregatorProcessor { get; set; }
        bool ScanForMesssageHandlers { get; set; }
        bool AutoStartConsuming { get; set; }
        string PersistenceStoreConnectionString { get; set; }
        string PersistenceStoreDatabaseName { get; set; }
        ITransportSettings TransportSettings { get; set; }
        IDictionary<string, IList<string>> QueueMappings { get; set; }
        Action<Exception> ExceptionHandler { get; set; }
        bool AddBusToContainer { get; set; }
        int Threads { get; set; }
        IList<Type> BeforeConsumingFilters { get; set; }
        IList<Type> AfterConsumingFilters { get; set; }
        IList<Type> OutgoingFilters { get; set; }
        bool EnableProcessManagerTimeouts { get; set; }

        /// <summary>
        /// Adds a message queue mapping. 
        /// </summary>
        /// <param name="messageType">Type of message</param>
        /// <param name="queue">Queue to send the message to</param>
        void AddQueueMapping(Type messageType, string queue);

        /// <summary>
        /// Adds message queue mappings. 
        /// </summary>
        /// <param name="messageType">Type of message</param>
        /// <param name="queues">Queues to send the message to</param>
        void AddQueueMapping(Type messageType, IList<string> queues);

        /// <summary>
        /// Set Exception handler. Exception handler is called when an exception is thrown while processing a message.
        /// </summary>
        /// <param name="exceptionHandler"></param>
        void SetExceptionHandler(Action<Exception> exceptionHandler);

        /// <summary>
        /// Sets the client host server
        /// </summary>
        /// <param name="host">Server connection string</param>
        void SetHost(string host);

        /// <summary>
        /// Sets the container.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IBusContainer.</typeparam>
        void SetContainerType<T>() where T : class, IBusContainer;

        /// <summary>
        /// Sets the process manager finder
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IProcessManagerFinder</typeparam>
        void SetProcessManagerFinder<T>() where T : class, IProcessManagerFinder;

        /// <summary>
        /// Set the aggregator persisitor
        /// </summary>
        /// <typeparam name="T"></typeparam>
        void SetAggregatorPersistor<T>() where T : class, IAggregatorPersistor;

        /// <summary>
        /// Sets the consumer type.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IConsumer.</typeparam>
        void SetConsumer<T>() where T : class, IConsumer;

        /// <summary>
        /// Sets the publisher type.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IPublisher.</typeparam>
        void SetProducer<T>() where T : class, IProducer;

        /// <summary>
        /// Sets QueueName
        /// </summary>
        void SetQueueName(string queueName);

        /// <summary>
        /// Sets ErrorQueueName
        /// </summary>
        void SetErrorQueueName(string errorQueueName);

        /// <summary>
        /// Sets AuditingEnabled
        /// </summary>
        void SetAuditingEnabled(bool auditingEnabled);

        /// <summary>
        /// Sets AuditQueueName
        /// </summary>
        void SetAuditQueueName(string auditQueueName);

        /// <summary>
        /// Sets HeartbeatQueueName
        /// </summary>
        void SetHeartbeatQueueName(string heartbeatQueueName);
        
        /// <summary>
        /// Gets queue name.
        /// </summary>
        /// <returns></returns>
        string GetQueueName();

        /// <summary>
        /// Gets error queue name.
        /// </summary>
        /// <returns></returns>
        string GetErrorQueueName();

        /// <summary>
        /// Gets audit queue name.
        /// </summary>
        /// <returns></returns>
        string GetAuditQueueName();

        /// <summary>
        /// Gets an instance of the publisher.
        /// </summary>
        /// <returns></returns>
        IProducer GetProducer();

        /// <summary>
        /// Gets an instance of the container.
        /// </summary>
        /// <returns></returns>
        IBusContainer GetContainer();

        /// <summary>
        /// Gets an instance of the ProcessManagerFinder
        /// </summary>
        /// <returns></returns>
        IProcessManagerFinder GetProcessManagerFinder();

        /// <summary>
        /// Gets an instance of the Aggregator Persistor
        /// </summary>
        /// <returns></returns>
        IAggregatorPersistor GetAggregatorPersistor();

        /// <summary>
        /// Gets a instance of the RequestConfiguration class.  Used to configure Request Reply messaging.
        /// </summary>
        /// <param name="requestMessageCorrelationId">Used to ensure the request is not proccessed as a reply</param>
        /// <returns>An instance of the RequestConfiguration class.</returns>
        IRequestConfiguration GetRequestConfiguration(Guid requestMessageCorrelationId);

        /// <summary>
        /// Disables publishing errors to error queue
        /// </summary>
        /// <returns></returns>
        void SetDisableErrors(bool disable);

        /// <summary>
        /// Removes all messages from the queue on startup
        /// </summary>
        /// <returns></returns>
        void PurgeQueuesOnStart();

        /// <summary>
        /// Gets an instance of the MessageBusReadStream
        /// </summary>
        /// <returns></returns>
        IMessageBusReadStream GetMessageBusReadStream();

        /// <summary>
        /// Gets an instance of the MessageBusWriteStream
        /// </summary>
        /// <returns></returns>
        IMessageBusWriteStream GetMessageBusWriteStream(IProducer producer, string endpoint, string sequenceId, IConfiguration configuration);

        /// <summary>
        /// Gets an instance of the AggregatorProcessor
        /// </summary>
        /// <param name="aggregatorPersistor"></param>
        /// <param name="container"></param>
        /// <param name="handlerType"></param>
        /// <returns></returns>
        IAggregatorProcessor GetAggregatorProcessor(IAggregatorPersistor aggregatorPersistor, IBusContainer container, Type handlerType);

        /// <summary>
        /// Sets the number of threads to consume messages on.
        /// </summary>
        /// <returns></returns>
        void SetThreads(int numberOfThreads);

  
        /// <summary>
        /// Creates a consumer to consume messages on.
        /// </summary>
        /// <returns></returns>
        IConsumer GetConsumer();
    }
}