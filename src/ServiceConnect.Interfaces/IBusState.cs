using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    public interface IBusState
    {
        IDictionary<Type, IAggregatorProcessor> AggregatorProcessors { get; set; }
        object ByteStreamLock { get; set; }
        IDictionary<string, IMessageBusReadStream> ByteStreams { get; set; }
        IDictionary<string, IRequestConfiguration> RequestConfigurations { get; set; }
        object RequestLock { get; set; }
    }
}