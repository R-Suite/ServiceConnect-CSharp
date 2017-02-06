[![Join the chat at https://gitter.im/R-Suite/ServiceConnect](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/R-Suite/ServiceConnect?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

**4.0.* (pre)release version supports .NET Core**

**_ServiceConnect 3.0.0 is available at [https://www.nuget.org/packages/ServiceConnect] (https://www.nuget.org/packages/ServiceConnect/)_**
* **New in ServiceConnect 3.0.0**
    - Support for multi-threaded consumers
    - SSL support
    - Intercept message-processing pipline with custom filters. See [Filters](https://github.com/R-Suite/ServiceConnect/tree/master/samples/Filters) sample application for a complete example.
    - Removed dependency on StructureMap. (To use StructureMap, pull down _ServiceConnect.Container.StructureMap_ nuget and set
     ```Bus.Initialize(config =>config.SetContainerType<StructureMapContainer>());``` when initializing the bus. You can also initialize the bus with your own container by specifying ```config.InitializeContainer(myStructureMapContainer)```)
    - Ninject container support ```Bus.Initialize(config =>config.SetContainerType<NinjectContainer>());```

A simple, easy to use asynchronous messaging framework for .NET.

In order to get started, have a look at the documentation at [http://serviceconnect.io/guides](http://serviceconnect.io/guides)

## Features

* Support for many well-known Enterprise Integration Patterns
    - Point to Point
    - Publish/Subscribe
    - Process Manager
    - Recipient List
    - Scatter Gather
    - Routing Slip
    - Message Aggregation
    - Content-Based Router
* Streaming
* Retries
* Auditing
* SSL Support
* Polymorphic message dispatch

## Project Maturity
ServiceConnect (recently renamed from R.MessageBus) has been first released in May 2014. The current version is used by a number of high-profile financial applications in production environments. Public API is stable and no major changes are planned in the next version.


## Simple example

In this example we simply send a message from one endpoint and consume the same message on another endpoint.
See [Point To Point](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/samples/PointToPoint) sample application for a complete example.

##### 1. Define your message

```YourMessage``` is a .Net class that inherits from
```ServiceConnect.Interfaces.Message``` base class

```c#
public class YourMessage : Message
{
    public YourMessage(Guid correlationId) : base(correlationId){}
}
```

##### 2. Send your message

In the standard command line ```Main``` method we start the bus with ```var bus = Bus.Initialize();```. Calling initialize with no parameters will create an instance of the Bus with default configuration options. Next, we simply send ```YourMessage``` using ```bus.Send(new YourMessage(id), "YourConsumer");```  - where the first argument is an instance of ```YourMessage```, the second argument, "YourConsumer", is the receiving enpoint name.  (We are going to configure "YourConsumer" next).

```c#
public class Program
{
    public static void Main()
    {
        var bus = Bus.Initialize();

        bus.Send(new YourMessage(Guid.NewGuid()), "YourConsumer");
    }
}
```

##### 3. Receive your message

Again,  we start the bus in the standard command line ```Main``` method. This time, however, with ```var bus = Bus.Initialize(config => config.SetEndPoint("YourConsumer"));```. Because the method initialize can also take a single lambda/action parameter for custom configuration, we explicitly set the name of the receiving endpoint to "YourConsumer".

```c#
public class Program
{
    public static void Main()
    {
        var bus = Bus.Initialize(config => config.SetEndPoint("YourConsumer"));
    }
}
```

Finally, we define a "handler" that will receive the message. The handler is a .NET class that implements ```ServiceConnect.Interfaces.IMessageHandler<T>``` where the generic parameter ```T``` is the type of the message being consumed.

```c#
public class YourMessageHandler : IMessageHandler<YourMessage>
{
    public void Execute(YourMessage message)
    {
        Console.WriteLine("Received message - {0}", message.CorrelationId);
    }
}
```
