using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication
{
    public static class AddMessageDeduplicationFilterExtensions
    {
        /// <summary>
        /// Registers the message deduplication filter, its persistor (based on
        /// <see cref="DeduplicationFilterSettings.PersistorType"/>), and the background
        /// cleanup service with the provided service collection.
        /// </summary>
        public static IServiceCollection AddMessageDeduplicationFilter(
            this IServiceCollection services,
            Action<DeduplicationFilterSettings> configure)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configure is null) throw new ArgumentNullException(nameof(configure));

            services.Configure(configure);

            services.AddSingleton<IMessageDeduplicationPersistor>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<DeduplicationFilterSettings>>().Value;
                return settings.PersistorType switch
                {
                    PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
                    PersistorType.MongoDb  => new MessageDeduplicationPersistorMongoDb(settings),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(settings.PersistorType), settings.PersistorType, "Unsupported persistor type.")
                };
            });

            services.AddTransient<OutgoingDeduplicationFilter>();
            services.AddTransient<IncomingDeduplicationFilter>();
            services.AddHostedService<DeduplicationCleanupHostedService>();

            return services;
        }
    }
}
