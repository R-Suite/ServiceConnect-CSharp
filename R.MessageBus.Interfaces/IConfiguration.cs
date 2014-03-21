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

        void SetConsumer<T>() where T : class, IConsumer;
        void SetContainer<T>() where T : class, IBusContainer;
        IConsumer GetConsumer();
        IBusContainer GetContainer();
    }
}