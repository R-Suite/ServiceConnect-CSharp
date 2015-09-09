using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IConsumerPool : IDisposable
    {
        void AddConsumer(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration consumer); 
    }
}