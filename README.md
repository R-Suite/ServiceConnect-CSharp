# ServiceConnect

[![NuGet](https://img.shields.io/nuget/v/ServiceConnect.svg)](https://www.nuget.org/packages/ServiceConnect/)

Asynchronous messaging for .NET. Distributed systems, done cleanly.

ServiceConnect is a thin, opinionated bus over RabbitMQ. It gives you the well-known Enterprise Integration Patterns — pub/sub, point-to-point, request/reply, process managers, aggregators, routing slips — behind a small async API that plugs into `Microsoft.Extensions.DependencyInjection`.

**📖 Full docs: [r-suite.github.io/ServiceConnect-CSharp](https://r-suite.github.io/ServiceConnect-CSharp/)**

## Install

```bash
dotnet add package ServiceConnect
dotnet add package ServiceConnect.Client.RabbitMQ
```

Optional extensions:

```bash
# Process-manager and aggregator persistence
dotnet add package ServiceConnect.Persistence.InMemory
dotnet add package ServiceConnect.Persistence.MongoDb

# Distributed tracing (W3C traceparent injection, OTel messaging semconv)
dotnet add package ServiceConnect.Telemetry

# Built-in filter
dotnet add package ServiceConnect.Filters.MessageDeduplication
```

## Quick start

Define a message:

```csharp
using ServiceConnect.Interfaces;

public sealed class OrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = "";
}
```

Write a handler:

```csharp
using ServiceConnect.Interfaces;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Received order {message.OrderId}");
        return Task.CompletedTask;
    }
}
```

Wire up the bus:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;

var services = new ServiceCollection();
services.AddLogging();

services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(transport =>
    {
        transport.Host = "localhost";
        transport.Username = "guest";
        transport.Password = "guest";
    });

    builder.ConfigureQueues(queues => queues.QueueName = "order-service");
});

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

// Start consuming, then publish
await bus.StartConsumingAsync();
await bus.PublishAsync(new OrderPlaced(Guid.NewGuid()) { OrderId = "ORD-001" });
```

## Messaging patterns

- **Publish/Subscribe** — broadcast events to every subscriber
- **Point-to-Point** — send commands to a specific endpoint
- **Request/Reply** — single-reply and multi-reply RPC
- **Competing Consumers** — scale out handlers across processes
- **Content-Based Routing** — dispatch by message type or content
- **Polymorphic Messages** — subscribe by base type and receive every derived message
- **Routing Slip** — sequential pipeline of endpoints
- **Scatter-Gather** — multicast with reply aggregation
- **Process Manager** — long-running, stateful workflows (sagas)
- **Aggregator** — accumulate related messages until complete
- **Streaming** — chunked delivery of large payloads
- **Filters & Middleware** — inspect, transform, or short-circuit the pipeline

Each pattern has a conceptual guide and worked example in [the docs](https://r-suite.github.io/ServiceConnect-CSharp/learn/).

## Examples

Runnable console apps live in [`examples/`](examples), one per pattern:

[PointToPoint](examples/PointToPoint) · [PublishSubscribe](examples/PublishSubscribe) · [RequestReply](examples/RequestReply) · [CompetingConsumers](examples/CompetingConsumers) · [ContentBasedRouting](examples/ContentBasedRouting) · [PolymorphicMessages](examples/PolymorphicMessages) · [RoutingSlip](examples/RoutingSlip) · [ScatterGather](examples/ScatterGather) · [Aggregator](examples/Aggregator) · [ProcessManager](examples/ProcessManager) · [Filters](examples/Filters) · [MessageDeduplication](examples/MessageDeduplication) · [Streaming](examples/Streaming) · [Telemetry](examples/Telemetry)

Each example ships with a `run.sh` and a `docker-compose.yml` at `examples/docker-compose.yml` for a local RabbitMQ broker.

## Supported runtimes

ServiceConnect targets modern .NET only — by design.

- **`net8.0`** — current LTS. The minimum.
- **`net10.0`** — current STS. Used to opt into recent BCL features (`System.Threading.Lock`, the `field` keyword) on the hot paths; net8.0 paths take guarded fallbacks.

The library packages (`ServiceConnect`, `ServiceConnect.Interfaces`, `ServiceConnect.Client.RabbitMQ`, `ServiceConnect.Persistence.*`, `ServiceConnect.Telemetry`) multi-target both. `ServiceConnect.Filters.MessageDeduplication` is currently `net10.0`-only.

We deliberately do **not** target `netstandard2.x`, `net6.0`, or `net7.0`:

- The hot paths use BCL features that are awkward to polyfill cleanly.
- The remaining LTS/STS surface (.NET 8 + .NET 10) covers every supported Microsoft runtime with active patch coverage at time of writing.
- Consumers on .NET Framework or out-of-support .NET Core SKUs can pin earlier ServiceConnect releases that targeted those runtimes; we are not adding support back to the current line.

If your scenario needs `netstandard2.1` (or you'd like to upstream the work), please open an issue.

## Other requirements

- RabbitMQ 3.7+

## License

MIT — see [LICENSE.md](LICENSE.md).
