using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Extension methods for registering RabbitMQ transport services with ServiceConnect.
/// </summary>
public static class RabbitMQExtensions
{
    /// <summary>
    /// Configures ServiceConnect to use the RabbitMQ transport implementation.
    /// </summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="configure">An optional callback used to customize the transport configuration.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
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
