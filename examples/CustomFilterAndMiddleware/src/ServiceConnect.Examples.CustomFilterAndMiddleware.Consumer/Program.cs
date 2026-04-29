using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Middleware;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(o => o.SingleLine = true);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<IDedupePersistor, InMemoryDedupePersistor>();
        services.AddTransient<DedupeIncomingFilter>();
        services.AddTransient<DedupeOnSuccessFilter>();
        services.AddTransient<LoggingTimingMiddleware>();
        services.AddTransient<OrderPlacedHandler>();

        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(queues =>
            {
                queues.QueueName = "custom-filter-and-middleware-sample";
            });
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
            });
            builder.AddBeforeConsumingFilter<DedupeIncomingFilter>();
            builder.AddOnConsumedSuccessfullyFilter<DedupeOnSuccessFilter>();
            builder.AddMessageProcessingMiddleware<LoggingTimingMiddleware>();
        });
    })
    .Build();

await host.RunAsync();
