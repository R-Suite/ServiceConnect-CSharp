**_Getting closer... Major/Production-ready release is planned for the end of March 2015._**

<img src="https://raw.githubusercontent.com/R-Suite/R.MessageBus/master/logo/logo.png" height="100">

A simple, easy to use asynchronous messaging framework for .NET.

## Features
* Support for many well-known Enteprise Integration Patterns
    - Point to Point
    - Publish/Subcribe
    - Process Manager
    - Recipient List
    - Scatter Gather
    - Routing Slip
    - Message Aggregation
* Streaming
* Retries
* Auditing


## Project Maturity
R.MessageBus is used by a number of high-profile financial applications in production environments. However,  it is still in early stages of development and hasn’t been officially released.  It may not yet be suitable for the most demanding and conservative projects. Production-ready release is planned for the end of March 2015

Public API is relatively stable and no major changes are planned in the next version.


## Getting Started

In order to get started, have a look at the documentation at [http://rmessagebus.com/guides](http://rmessagebus.com/guides)

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


