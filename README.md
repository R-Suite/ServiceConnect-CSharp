# R.MessageBus
A simple, easy to use asynchronous messaging framework for .NET built on RabbitMQ.

## Features
* Point to Point
* Publish/Subcribe
* Process Manager
* Retries
* Dead Letter Queue

## Dependancies
* Structuremap = 0.0.0
* RabbitMQ client = 0.0.0
* Log4Net = 0.0.0

## Getting Started

### Configuration

#### Simple Configuration

Calling initialize will create an instance of the Bus with default configuration options.  

	IBus bus = Bus.Initialize();

Default configuration is the following,

* Consumer - RabbitMQ
* Container - StructureMap
* ScanForMessageHandlers - False
* ConfigurationPath - Applications default configuration file path.
* EndPoint - null

#### Custom Configuration

	IBus bus = Bus.Initialize(config =>
	{
	    config.EndPoint = "MyEndpoint";
	    config.ConfigurationPath = "MyConfigurationPath";
	    config.SetConsumer<Consumer>();
	    config.SetContainer<Container>();
	    ScanForMesssageHandlers = true;
	});

### Point To Point

### Publish/Subscribe

### Process Manager

### Retries

### Dead Letter Queue
