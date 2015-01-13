# R.MessageBus
A simple, easy to use asynchronous messaging framework for .NET.

## Features
* Point to Point
* Publish/Subcribe
* Process Manager
* Retries
* Dead Letter Queue


## Project Maturity
R.MessageBus is used by a number of high-profile financial applications in production environments. However,  it is still in early stages of development and hasn’t been officially released.  It may not yet be suitable for the most demanding and conservative projects.

Public API is relatively stable and no major changes are planned in the next version.


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

#### Command Definition

```c#
public class PointToPointMessage : Message
{
    public PointToPointMessage(Guid correlationId) : base(correlationId){}
}
```

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

![Publish-Subscribe](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/PublishSubscribe.gif)

A Publish-Subscribe channel works like this: It has one input channel that splits into multiple output channels, one for each subscriber.  When an event is published into the channel, the Publish-Subscribe Channel, delivers a copy of the message to each of the output channels.  Each output end of the channel has only one subscriber, which is allowed to consume a message only once.  In this way, each subscriber gets the message only once, and consumed copies disapear from their channels.

See *Enterprise Integration Patterns (G. Hohpe, B. Woolf, 2009: 106-110)* for more details.

#### Event Definition

```c#
public class PublishSubscribeMessage : Message
{
    public PublishSubscribeMessage(Guid correlationId) : base(correlationId){}
}
```

#### Publishing Events

```c#
var bus = Bus.Initialize();
bus.Publish(new PublishSubscribeMessage(id));
```

#### Consuming Events

```c#
public class PublishSubscribeMessageHandler : IMessageHandler<PublishSubscribeMessage>
{
    public void Execute(PublishSubscribeMessage message)
    {
        Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);
    }
}
```

See [Publish - Subscribe](../../tree/master/samples/PublishSubscribe) sample application for a complete example.

### Process Manager

![ProcessManager](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/ProcessManager.gif)

Using a Process Manager results in a so-called hub-and-spoke pattern of message flow. An incoming message initializes the Process Manager.  We call this message the trigger message.  Based on the rules inside the process manager it sends a message to processing steps which then send reply messages back.  When receiving messages the Process Manager determines the next step to be executed.  As a result, all traffic runs through this central hub, hence the term hub-and-spoke.  The downside of this central control element is the danger of turning the Process Manager into a performance bottleneck.

See *Enterprise Integration Patterns (G. Hohpe, B. Woolf, 2009: 312-321)* for more details.

#### Creating the Process Manager

```c#
public class MealData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public bool BurgerCooked { get; set; }
    public bool FoodPrepped { get; set; }
    public string Meal { get; set; }
    public string Size { get; set; }
}
```

```c#
public class MealProcess : ProcessManager<MealData>, IStartProcessManager<NewOrderMessage>,
                                                     IMessageHandler<BurgerCookedMessage>,
                                                     IMessageHandler<FoodPrepped>
{
    private readonly IBus _bus;

    public Meal(IBus bus)
    {
        _bus = bus;
    }

    public void Execute(NewOrderMessage message)
    {
        Data.CorrelationId = Guid.NewGuid();
        Data.Meal = message.Name;
        Data.Size = message.Size;

        var prepFoodMessage = new PrepFoodMessage(Data.CorrelationId)
        {
            BunSize = message.Size
        };
        _bus.Publish(prepFoodMessage);

        var flipBurgerMessage = new CookBurgerMessage(Data.CorrelationId)
        {
            BurgerSize = message.Size
        };
        _bus.Publish(flipBurgerMessage);
    }

    public void Execute(BurgerCookedMessage message)
    {
        Data.BurgerCooked = true;
        if (Data.FoodPrepped)
        {
            _bus.Publish(new OrderReadyMessage(message.CorrelationId)
            {
                Size = Data.Size,
                Meal = Data.Meal
            });
            Complete = true;
        }
    }

    public void Execute(FoodPrepped message)
    {
        Data.FoodPrepped = true;
        if (Data.BurgerCooked)
        {
            _bus.Publish(new OrderReadyMessage(message.CorrelationId)
            {
                Size = Data.Size,
                Meal = Data.Meal
            });
            Complete = true;
        }
    }
}
```

See [McDonalds - Process Manager](../../tree/master/samples/McDonalds) sample application for a complete example.

### Request Reply

![Request Reply](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/RequestReply.gif)

```c#
public class RequestMessage : Message
{
    public RequestMessage(Guid correlationId) : base(correlationId)  { }
}
    
public class ResponseMessage : Message
{
    public ResponseMessage(Guid correlationId) : base(correlationId) { }
}
```

#### Synchronous Block

```c#
ResponseMessage result = bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(Guid.NewGuid()));
```

#### Asynchronous Callback

```c#
var message = new RequestMessage(Guid.NewGuid());
bus.SendRequest<RequestMessage, ResponseMessage>("Responder", message, r => Console.WriteLine("Received reply - {0}", r.CorrelationId));
```

#### Replier

```c#
public class RequestMessageHandler : IMessageHandler<RequestMessage>
{
    public IConsumeContext Context { get; set; }

    public void Execute(RequestMessage message)
    {
        Context.Reply(new ResponseMessage(message.CorrelationId));
    }
}
```

See [Request Response](../../tree/master/samples/RequestResponse) sample application for a complete example.


### Routing Slip

![Request Reply](https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/images/RoutingTableSimple.gif)

Attach a Routing Slip to each message, specifying the sequence of processing steps. Wrap each component with a special message router that reads the Routing Slip and routes the message to the next component in the list.

```c#
public class RoutingSlipMessage : Message
{
    public RoutingSlipMessage(Guid correlationId) : base(correlationId)  { }
}
```

#### Routing messages

```c#
bus.Route(new RoutingSlipMessage(id), new List<string> { "RoutingSlip.Endpoint1", "RoutingSlip.Endpoint2" });
```

#### Consuming messages

Consuming messages with Routing Slip is no different from consuming standard events or commands.

```c#
public class RoutingSlipMessageHandler : IMessageHandler<RoutingSlipMessage>
{
    public void Execute(RoutingSlipMessage message)
    {
        Console.WriteLine("Endpoint1 received message - {0}", message.CorrelationId);
    }

    public IConsumeContext Context { get; set; }
}
```

See [Routing Slip](../../tree/master/samples/RoutingSlip) sample application for a complete example.


### Retries

When your application fails to successfully proccess a message, R.MessageBus implements a generic error handling for all your consumers. Upon catching an exception, the message is held in the "*.Retries" queue for a certain amount of time before being requeued. This process is repeated a number of times until either the message is handled successfully, or the "MaxRetries" limit is reached, at which point the message is moved to the error queue.

By default, "MaxRetries" is set to 3 and "RetryDelay" is set to 3000 milliseconds. The default values can be overridden in your application's configuration file, or by assigning properties on TransportSettings object prior to instantiating consumers.

```c#
bus.Configuration.TransportSettings.MaxRetries = 5;
bus.Configuration.TransportSettings.RetryDelay = 50000;
```

### Dead Letter Queue
