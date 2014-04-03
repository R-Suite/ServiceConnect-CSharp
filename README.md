# R.MessageBus
A simple, easy to use asynchronous messaging framework for .NET.

## Features
* Point to Point
* Publish/Subcribe
* Process Manager
* Retries
* Dead Letter Queue

## Getting Started

### Configuration

#### Simple Configuration

Calling initialize with no parameters will create an instance of the Bus with default configuration options.

```c#
IBus bus = Bus.Initialize();
```

Default configuration is the following,

* Consumer - RabbitMQ
* Container - StructureMap
* ScanForMessageHandlers - False

#### Custom Configuration

Initialize also takes a single lambda/action parameter for custom configuration.

```c#
IBus bus = Bus.Initialize(config =>
{
    config.LoadSettings("MyConfigurationPath", "MyEndpoint");
    config.SetConsumer<Consumer>();
    config.SetPublisher<Publisher>();
    config.SetContainer<Container>();
    config.ScanForMesssageHandlers = true;
});
```

### Point To Point

![Point To Point](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/PointToPoint.gif)

A Point to Point channel ensures that only one receiver consumes any given message.  The channel can have multiple receivers that can consume multiple messages concurrently (Competing Consumers), but only one of them can successfully consume a particular message.  This design makes consuming and processing messages highly scalable because the work can be load balanced across multiple consumers running in multiple applications on multiple computers.

See *Enterprise Integration Patterns (G. Hohpe, B. Woolf, 2009: 103-105, 502-507)* for more details.

#### Sending Commands

```c#
var bus = Bus.Initialize(config => 
	config.AddEndPointMapping(typeof(PointToPointMessage), "PointToPoint")
);
bus.Send(new PointToPointMessage(id));
```

#### Consuming Commands

```c#
var bus = Bus.Initialize(config => config.SetEndPoint("PointToPoint"));
bus.StartConsuming();
```

```c#
public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
{
    public void Execute(PointToPointMessage command)
    {
        Console.WriteLine("Received message - {0}", command.CorrelationId);
    }
}
```

See [Point To Point](../../tree/master/samples/PointToPoint) and [Competing Consumers](../../tree/master/samples/CompetingConsumers) sample applications for a complete example.

### Publish/Subscribe

![Publish-Subscribe](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/Publish-Subscribe.png)

A Publish-Subscribe channel works like this: It has one input channel that splits into multiple output channels, one for each subscriber.  When an event is published into the channel, the Publish-Subscribe Channel, delivers a copy of the message to each of the output channels.  Each output end of the channel has only one subscriber, which is allowed to consume a message only once.  In this way, each subscriber gets the message only once, and consumed copies disapear from their channels.

See *Enterprise Integration Patterns (G. Hohpe, B. Woolf, 2009: 106-110)* for more details.

### Process Manager

![ProcessManager](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/ProcessManager.png)

### Retries

### Dead Letter Queue
