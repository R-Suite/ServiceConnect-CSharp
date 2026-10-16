[![Join the chat at https://gitter.im/R-Suite/ServiceConnect](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/R-Suite/ServiceConnect?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

**_ServiceConnect 7.0.0 is available at [https://www.nuget.org/packages/ServiceConnect](https://www.nuget.org/packages/ServiceConnect/)_**
* **New in ServiceConnect 7.0.0**
    - Targets .NET 10
    - MongoDB persistence runs on MongoDB.Driver 3
    - Dependencies upgraded to versions with no known vulnerabilities

See [Upgrading to ServiceConnect 7](#upgrading-to-serviceconnect-7) before you upgrade.

ServiceConnect is a simple, easy to use asynchronous messaging framework for .NET.

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
* .NET Core
* SSL Support
* Polymorphic message dispatch
* Multi-threaded consumers
* Intercept message-processing pipline with custom filters. See [Filters](https://github.com/R-Suite/ServiceConnect/tree/master/samples/Filters) sample application for a complete example.

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

## Upgrading to ServiceConnect 7

These packages target `net10.0` only. Applications on .NET 6 or .NET 8 must stay on the earlier versions.

| Package | New version |
| --- | --- |
| `ServiceConnect` | 7.0.0 |
| `ServiceConnect.Client.RabbitMQ` | 7.0.0 |
| `ServiceConnect.Interfaces` | 7.0.0 |
| `ServiceConnect.Container.ServiceCollection` | 7.0.0 |
| `ServiceConnect.Container.StructureMap` | 7.0.0 |
| `ServiceConnect.Persistance.MongoDb` | 8.0.0 |
| `ServiceConnect.Persistance.MongoDbSsl` | 8.0.0 |

### MongoDB persistence

Both MongoDB packages now use MongoDB.Driver 3.12. `ServiceConnect.Persistance.MongoDb` no longer depends on the legacy `mongocsharpdriver` package.

Earlier versions stored GUIDs as `CSharpLegacy` (binary subtype 3). MongoDB.Driver 3 has no default GUID representation, so the persistence classes register a `CSharpLegacy` Guid serializer the first time you create one. If your application registers its own Guid serializer, do it before you create the persistence classes. Use `CSharpLegacy` to keep reading data written by earlier versions.

You no longer need to register an `ObjectSerializer` that allows your process manager data and message types. The persistence classes allow any `IProcessManagerData` or `Message` type in the data they store.

The `nodes` setting of the `MongoDbSsl` connection string now accepts `host:port`. A host without a port still uses 27017.

These bugs are fixed:

* `MongoDbSslProcessManagerFinder.GetTimeoutsBatch` threw for every due timeout and left it locked, so timeouts never fired.
* `MongoDbSslProcessManagerFinder.RemoveDispatchedTimeout` could remove a different locked timeout from the one it was given.
* `MongoDbProcessManagerFinder.InsertData` added a new document on every call instead of replacing the one with the same correlation ID.

### SQL Server persistence

`SqlServerProcessManagerFinder` now uses `Microsoft.Data.SqlClient` instead of the deprecated `System.Data.SqlClient`. `Microsoft.Data.SqlClient` encrypts connections by default and checks the server certificate. If your SQL Server uses a certificate your machine does not trust, the connection fails with `UntrustedRoot`. Install a trusted certificate, or add `TrustServerCertificate=True` to the connection string.

### Packages

The `ServiceConnect` package no longer contains copies of `ServiceConnect.Client.RabbitMQ.dll` and `ServiceConnect.Interfaces.dll`. They come from their own packages, which `ServiceConnect` depends on.

## Running the tests

Run the unit tests from the `src` folder:

```bash
dotnet test ServiceConnect.UnitTests
```

The integration tests need real servers. Each test is skipped when the server it needs is not configured.

1. To start a plain MongoDB server and a MongoDB server that requires TLS, run the script. It needs Docker and OpenSSL, and prints the environment variables to export.

   ```bash
   scripts/start-test-mongo.sh
   ```

2. To run the SQL Server tests, start a SQL Server and set `SERVICECONNECT_SQLSERVER`:

   ```bash
   docker run -d --name serviceconnect-test-sqlserver -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<password>' -p 14333:1433 mcr.microsoft.com/mssql/server:2019-latest
   export SERVICECONNECT_SQLSERVER="Server=localhost,14333;User ID=sa;Password=<password>;TrustServerCertificate=True"
   ```

3. The bus tests connect to RabbitMQ on `localhost:5672` with the default `guest` account.

4. Run the integration tests:

   ```bash
   dotnet test ServiceConnect.IntegrationTests
   dotnet test ServiceConnect.IntegrationTestsSsl
   ```

To stop the MongoDB servers, run `scripts/start-test-mongo.sh --stop`.
