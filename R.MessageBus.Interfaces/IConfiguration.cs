using System;

namespace R.MessageBus.Interfaces
{
    public interface IConfiguration
    {
        Type Container { get; set; }
        Type ConsumerType { get; set; }
        string EndPoint { get; set; }
        string ConfigurationPath { get; set; }
        bool ScanForMesssageHandlers { get; set; }

        /// <summary>
        /// Sets the consumer type.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IConsumer.</typeparam>
        void SetConsumer<T>() where T : class, IConsumer;

        /// <summary>
        /// Sets the container.
        /// </summary>
        /// <typeparam name="T">The type must be a class that implements IBusContainer.</typeparam>
        void SetContainer<T>() where T : class, IBusContainer;

        /// <summary>
        /// Gets an instance of the consumer.
        /// </summary>
        /// <returns></returns>
        IConsumer GetConsumer();

        /// <summary>
        /// Gets an instance of the container.
        /// </summary>
        /// <returns></returns>
        IBusContainer GetContainer();
    }
}