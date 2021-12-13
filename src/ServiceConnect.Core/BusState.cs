using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;

namespace ServiceConnect.Core
{
    public class BusState : IBusState
    {
        public IDictionary<string, IRequestConfiguration> RequestConfigurations { get; set; }
        public IDictionary<string, IMessageBusReadStream> ByteStreams { get; set; }
        public object RequestLock { get; set; }
        public object ByteStreamLock { get; set; }
        public IDictionary<Type, IAggregatorProcessor> AggregatorProcessors { get; set; }

        public BusState()
        {
            RequestConfigurations = new Dictionary<string, IRequestConfiguration>();
            ByteStreams = new Dictionary<string, IMessageBusReadStream>();
            RequestLock = new object();
            ByteStreamLock = new object();
            AggregatorProcessors = new Dictionary<Type, IAggregatorProcessor>();
        }
    }
}
