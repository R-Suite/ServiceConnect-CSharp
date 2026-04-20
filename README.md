# ServiceConnect

[![NuGet](https://img.shields.io/nuget/v/ServiceConnect.svg)](https://www.nuget.org/packages/ServiceConnect/)

ServiceConnect is a simple, easy-to-use asynchronous messaging framework for .NET. Built on top of RabbitMQ, it provides a clean abstraction for building distributed systems using well-known Enterprise Integration Patterns.

## What is it for?

ServiceConnect enables you to build loosely-coupled, asynchronous applications in .NET. It's ideal for:

- **Microservices Communication** - Send messages between services without direct dependencies
- **Event-Driven Architecture** - Publish events to multiple subscribers
- **Distributed Systems** - Build systems that span multiple processes or machines
- **CQRS Implementation** - Separate read and write concerns through messaging

## Installation

Install via NuGet:

```bash
dotnet add package ServiceConnect
dotnet add package ServiceConnect.Client.RabbitMQ
```

## Quick Start

### 1. Define a Message

```csharp
using ServiceConnect.Interfaces;

public class YourMessage : Message
{
    public YourMessage(Guid correlationId) : base(correlationId) { }
    
    public string Content { get; set; }
}
```

### 2. Create a Consumer

```csharp
using ServiceConnect.Interfaces;

public class YourMessageHandler : IMessageHandler<YourMessage>
{
    public void Execute(YourMessage message)
    {
        Console.WriteLine($"Received: {message.Content}");
    }
}
```

### 3. Send a Message

```csharp
var bus = Bus.Initialize();
bus.Send(new YourMessage(Guid.NewGuid()), "YourEndpoint");
```

### 4. Receive Messages

```csharp
var bus = Bus.Initialize(config => 
    config.SetEndpoint("YourEndpoint")
          .ScanForMessageHandlers());
```

## Features

### Enterprise Integration Patterns

- **Point-to-Point** - Send messages to a specific endpoint
- **Publish/Subscribe** - Broadcast messages to multiple consumers
- **Process Manager** - Coordinate multi-step workflows
- **Routing Slip** - Route messages through a sequence of endpoints
- **Scatter-Gather** - Send to multiple recipients and collect responses
- **Message Aggregation** - Combine multiple messages into one
- **Content-Based Routing** - Route based on message content

### Additional Features

- Asynchronous message handlers
- Priority queue support
- Automatic retries with configurable delays
- Message auditing
- SSL/TLS support
- Polymorphic message dispatch
- Multi-threaded consumers
- Message filtering pipeline
- Streaming support

## Configuration

```csharp
var bus = Bus.Initialize(config =>
{
    config.SetEndpoint("MyEndpoint");
    config.SetHost("localhost");
    config.SetUsername("guest");
    config.SetPassword("guest");
    config.ScanForMessageHandlers();
    config.SetMaxRetries(3);
    config.SetRetryDelay(3000);
    config.EnableAuditing();
});
```

## Container Support

ServiceConnect supports multiple IoC containers:

- `ServiceConnect.Container.Default` - Built-in container
- `ServiceConnect.Container.ServiceCollection` - Microsoft.Extensions.DependencyInjection
- `ServiceConnect.Container.StructureMap` - StructureMap
- `ServiceConnect.Container.Ninject` - Ninject

## Persistence

Choose a persistence store for process managers and aggregators:

- `ServiceConnect.Persistance.InMemory` - In-memory storage (development)
- `ServiceConnect.Persistance.MongoDb` - MongoDB
- `ServiceConnect.Persistance.SqlServer` - SQL Server
- `ServiceConnect.Persistance.MongoDbSsl` - MongoDB with SSL

## Examples

Check out the [examples](examples) directory for runnable console applications covering the supported messaging patterns:

- [PointToPoint](examples/PointToPoint) - Basic send/receive
- [PublishSubscribe](examples/PublishSubscribe) - Pub/Sub messaging
- [RequestReply](examples/RequestReply) - Request/reply pattern
- [CompetingConsumers](examples/CompetingConsumers) - Multiple workers on one queue
- [ContentBasedRouting](examples/ContentBasedRouting) - Route by published message type
- [RoutingSlip](examples/RoutingSlip) - Sequential routing
- [ScatterGather](examples/ScatterGather) - Multicast with multiple replies
- [Aggregator](examples/Aggregator) - Mongo-backed message aggregation
- [ProcessManager](examples/ProcessManager) - Mongo-backed workflow orchestration
- [Filters](examples/Filters) - Custom message processing pipeline
- [Streaming](examples/Streaming) - Chunked message streaming

## Requirements

- .NET 10.0+
- RabbitMQ 3.7+

## License

Licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
