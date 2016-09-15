using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    public interface IConsumerPool : IDisposable
    {
        void AddConsumer(string queueName, IDictionary<string, IList<string>> messageTypes, ConsumerEventHandler eventHandler, IConfiguration consumer); 
    }
}