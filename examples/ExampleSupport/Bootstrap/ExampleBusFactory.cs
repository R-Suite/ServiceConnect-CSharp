using Microsoft.Extensions.DependencyInjection;
using ServiceConnect;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Persistence.MongoDb;

namespace ServiceConnect.Examples.Support.Bootstrap;

public static class ExampleBusFactory
{
    public static IServiceCollection AddExampleBus(
        this IServiceCollection services,
        ExampleSettings settings,
        string queueName,
        bool useMongoDb = false,
        string? databaseName = null,
        Action<IQueueConfiguration>? configureQueues = null,
        Action<ServiceConnectBuilder>? configureBuilder = null)
    {
        services.AddLogging();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = settings.RabbitMqHost;
                transport.Username = settings.RabbitMqUsername;
                transport.Password = settings.RabbitMqPassword;
                transport.SetClientSetting("Port", settings.RabbitMqPort);
                transport.SetClientSetting("RetryCount", 3);
                transport.SetClientSetting("RetrySeconds", 1);
                transport.SslEnabled = false; // local-dev plaintext; production must use TLS
            });

            builder.ConfigureQueues(queues =>
            {
                queues.QueueName = queueName;
                configureQueues?.Invoke(queues);
            });
            builder.ConfigureBus(bus => bus.ScanForMessageHandlers = false);

            if (useMongoDb)
            {
                builder.UseMongoDbPersistence(options =>
                {
                    options.ConnectionString = settings.MongoConnectionString;
                    options.DatabaseName = databaseName ?? queueName.Replace('-', '_');
                });
            }

            configureBuilder?.Invoke(builder);
        });

        return services;
    }
}
