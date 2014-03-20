using System;

namespace R.MessageBus.Interfaces
{
    public interface IConfiguration
    {
        IBusContainer Container { get; set; }
        Type ConsumerType { get; set; }
        string EndPoint { get; set; }
        string ConfigurationPath { get; set; }

        void SetConsumer<T>() where T : class, IConsumer;
        void SetContainer<T>() where T : class, IBusContainer;
        IConsumer GetConsumer();
    }
}