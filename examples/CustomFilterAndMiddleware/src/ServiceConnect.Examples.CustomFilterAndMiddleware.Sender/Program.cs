using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(queues =>
            {
                queues.QueueName = "custom-filter-and-middleware-sender";
            });
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
                transport.SslEnabled = false; // local-dev plaintext; production must use TLS
            });
        });
    })
    .Build();

await host.StartAsync();

var bus = host.Services.GetRequiredService<IBus>();

var endpoint = "custom-filter-and-middleware-sample";

// Note: OrderPlaced uses a primary-ctor `correlationId` (Message base class).
// The sample uses a fresh Guid per message; correlation tracing is out of scope here.

// Scenario 1: a normal message — handler runs, on-success filter records.
await bus.SendAsync(
    new OrderPlaced(Guid.NewGuid()) { OrderId = "order-1", Amount = 42.50m },
    new SendOptions { EndPoint = endpoint });

// Scenario 2: handler crashes once, broker redelivers, second attempt succeeds.
// On-success does NOT record on the failed first attempt; redelivery proceeds.
await bus.SendAsync(
    new OrderPlaced(Guid.NewGuid()) { OrderId = "crash-once", Amount = 99.00m },
    new SendOptions { EndPoint = endpoint });

// One more normal message so the consumer log shows the timing middleware repeatedly.
await bus.SendAsync(
    new OrderPlaced(Guid.NewGuid()) { OrderId = "order-2", Amount = 7.00m },
    new SendOptions { EndPoint = endpoint });

Console.WriteLine("Sender: published three messages, exiting.");
await host.StopAsync();
