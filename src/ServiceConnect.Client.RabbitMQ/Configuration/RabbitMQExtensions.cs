using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Client.RabbitMQ.Configuration;
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

    /// <summary>
    /// Configures ServiceConnect to use the RabbitMQ transport implementation with strongly-typed options.
    /// The <paramref name="configure"/> lambda receives a fresh <see cref="RabbitMqOptions"/>;
    /// non-null properties are written into <see cref="ITransportConfiguration.ClientSettings"/>
    /// using the keys from <see cref="RabbitMQSettingKeys"/>. Settings left null are not written,
    /// leaving any prior <c>SetClientSetting</c> values or runtime defaults in place.
    /// </summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="configure">An optional callback used to set strongly-typed RabbitMQ options.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static ServiceConnectBuilder UseRabbitMQ(
        this ServiceConnectBuilder builder,
        Action<RabbitMqOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            var options = new RabbitMqOptions();
            configure(options);

            var validationErrors = options.Validate();
            if (validationErrors.Count > 0)
            {
                throw new ArgumentException(
                    "RabbitMqOptions contains invalid values: " + string.Join("; ", validationErrors),
                    nameof(configure));
            }

            builder.ConfigureTransport(transport => ApplyToClientSettings(transport, options));
        }

        builder.AddRegistration(services =>
        {
            services.TryAddSingleton<IProducer, Producer>();
            services.TryAddSingleton<IConsumer, Consumer>();
        });

        return builder;
    }

    // Maps non-null RabbitMqOptions properties to ClientSettings entries using the
    // RabbitMQSettingKeys constants. Null properties are intentionally skipped so callers
    // can partially override settings without inadvertently clearing unrelated values.
    private static void ApplyToClientSettings(ITransportConfiguration transport, RabbitMqOptions options)
    {
        if (options.Port.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.Port, options.Port.Value); }
        if (options.Durable.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.Durable, options.Durable.Value); }
        if (options.Exclusive.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.Exclusive, options.Exclusive.Value); }
        if (options.AutoDelete.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.AutoDelete, options.AutoDelete.Value); }
        if (options.Arguments is not null) { transport.SetClientSetting(RabbitMQSettingKeys.Arguments, options.Arguments); }
        if (options.RetryQueueArguments is not null) { transport.SetClientSetting(RabbitMQSettingKeys.RetryQueueArguments, options.RetryQueueArguments); }
        if (options.UtilityQueueArguments is not null) { transport.SetClientSetting(RabbitMQSettingKeys.UtilityQueueArguments, options.UtilityQueueArguments); }
        if (options.PrefetchCount.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.PrefetchCount, options.PrefetchCount.Value); }
        if (options.DisablePrefetch.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.DisablePrefetch, options.DisablePrefetch.Value); }
        if (options.MessageSize.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MessageSize, options.MessageSize.Value); }
        if (options.PublisherAcknowledgements.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, options.PublisherAcknowledgements.Value); }
        if (options.RetryCount.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.RetryCount, options.RetryCount.Value); }
        if (options.RetrySeconds.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.RetrySeconds, options.RetrySeconds.Value); }
        if (options.HeartbeatEnabled.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.HeartbeatEnabled, options.HeartbeatEnabled.Value); }
        if (options.HeartbeatTime.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.HeartbeatTime, options.HeartbeatTime.Value); }
        if (options.PublishTimeout.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.PublishTimeout, options.PublishTimeout.Value); }
        if (options.MaxOutstandingPublishConfirms.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, options.MaxOutstandingPublishConfirms.Value); }
        if (options.NetworkRecoveryInterval.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.NetworkRecoveryInterval, options.NetworkRecoveryInterval.Value); }
        if (options.MaxHeaderCount.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxHeaderCount, options.MaxHeaderCount.Value); }
        if (options.MaxHeaderValueBytes.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxHeaderValueBytes, options.MaxHeaderValueBytes.Value); }
    }
}
