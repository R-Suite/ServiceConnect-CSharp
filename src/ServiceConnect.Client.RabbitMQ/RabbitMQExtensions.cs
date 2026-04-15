using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public static class RabbitMQExtensions
{
    public static ServiceConnectBuilder UseRabbitMQ(
        this ServiceConnectBuilder builder,
        Action<ITransportConfiguration>? configure = null)
    {
        if (configure != null)
        {
            builder.ConfigureTransport(configure);
        }

        builder.AddRegistration(services =>
        {
            services.TryAddSingleton<IProducer, Producer>();
            services.TryAddSingleton<IConsumer, Consumer>();
        });

        return builder;
    }
}
