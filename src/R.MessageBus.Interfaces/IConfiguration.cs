using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IConfiguration
    {
        Type ConsumerType { get; set; }
        Type ProducerType { get; set; }
        Type Container { get; set; }
        Type ProcessManagerFinder { get; set; }
        bool ScanForMesssageHandlers { get; set; }
        bool AutoStartConsuming { get; set; }
        string PersistenceStoreConnectionString { get; set; }
        string PersistenceStoreDatabaseName { get; set; }
        ITransportSettings TransportSettings { get; set; }
        IDictionary<string, IList<string>> QueueMappings { get; set; }
        Action<Exception> ExceptionHandler { get; set; }
        bool AddBusToContainer { get; set; }

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
        /// Load configuration from file path an initialize Transport Settings
        /// </summary>;
        /// <param name="configFilePath"></param>
        /// <param name="endPoint"></param>
        void LoadSettings(string configFilePath = null, string endPoint = null);

        /// <summary>
        /// Sets the container.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IBusContainer.</typeparam>
        void SetContainer<T>() where T : class, IBusContainer;

        /// <summary>
        /// Sets the process manager finder
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IProcessManagerFinder</typeparam>
        void SetProcessManagerFinder<T>() where T : class, IProcessManagerFinder;

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
        /// Gets an instance of the consumer.
        /// </summary>
        /// <returns></returns>
        IConsumer GetConsumer();

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
        /// Gets a instance of the RequestConfiguration class.  Used to configure Request Reply messaging.
        /// </summary>
        /// <param name="consumeMessageEvent">The message event handler to call when receiving a reply.</param>
        /// <param name="requestMessageCorrelationId">Used to ensure the request is not proccessed as a reply</param>
        /// <param name="messageType">Type of response message</param>
        /// <returns>An instance of the RequestConfiguration class.</returns>
        IRequestConfiguration GetRequestConfiguration(ConsumerEventHandler consumeMessageEvent, Guid requestMessageCorrelationId, string messageType);

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
    }
}