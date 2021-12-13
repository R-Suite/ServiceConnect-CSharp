using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace ServiceConnect.Core
{
    public class BusState : IBusState
    {
        public IDictionary<string, IRequestConfiguration> RequestConfigurations { get; set; } = new Dictionary<string, IRequestConfiguration>();
        public IDictionary<string, IMessageBusReadStream> ByteStreams { get; set; } = new Dictionary<string, IMessageBusReadStream>();
        public object RequestLock { get; set; } = new object();
        public object ByteStreamLock { get; set; } = new object();
        public IDictionary<Type, IAggregatorProcessor> AggregatorProcessors { get; set; } = new Dictionary<Type, IAggregatorProcessor>();
    }
}
