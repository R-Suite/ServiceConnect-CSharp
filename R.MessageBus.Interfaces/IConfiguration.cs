using System;

namespace R.MessageBus.Interfaces
{
    public interface IConfiguration
    {
        Type Container { get; set; }
        Type ConsumerType { get; set; }
        Type PublisherType { get; set; }
        Type ProcessManagerFinder { get; set; }
        bool ScanForMesssageHandlers { get; set; }
        string PersistenceStoreConnectionString { get; set; }
        string PersistenceStoreDatabaseName { get; set; }
        ITransportSettings TransportSettings { get; set; }

        /// <summary>
        /// Load configuration from file path an initialize Transport Settings
        /// </summary>
        /// <param name="configFilePath"></param>
        /// <param name="endPoint"></param>
        void LoadSettings(string configFilePath = null, string endPoint = null);

        /// <summary>
        /// Sets the consumer type.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IConsumer.</typeparam>
        void SetConsumer<T>() where T : class, IConsumer;

        /// <summary>
        /// Sets the publisher type.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IPublisher.</typeparam>
        void SetPublisher<T>() where T : class, IPublisher;

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
        /// Gets an instance of the consumer.
        /// </summary>
        /// <returns></returns>
        IConsumer GetConsumer();

        /// <summary>
        /// Gets an instance of the publisher.
        /// </summary>
        /// <returns></returns>
        IPublisher GetPublisher();

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
    }
}