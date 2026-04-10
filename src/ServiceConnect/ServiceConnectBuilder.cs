using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

public class ServiceConnectBuilder
{
    internal BusConfiguration BusConfig { get; } = new();
    public List<Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>> AdditionalRegistrations { get; } = new();

    public ServiceConnectBuilder ConfigureTransport(Action<ITransportConfiguration> configure)
    {
        configure(BusConfig.Transport);
        return this;
    }

    public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
    {
        configure(BusConfig.Queues);
        return this;
    }

    public ServiceConnectBuilder ConfigurePersistence(Action<IPersistenceConfiguration> configure)
    {
        configure(BusConfig.Persistence);
        return this;
    }

    public ServiceConnectBuilder ConfigurePipeline(Action<IPipelineConfiguration> configure)
    {
        configure(BusConfig.Pipeline);
        return this;
    }

    public ServiceConnectBuilder ConfigureBus(Action<IBusConfiguration> configure)
    {
        configure(BusConfig);
        return this;
    }

    public ServiceConnectBuilder AddOutgoingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.OutgoingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddBeforeConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.BeforeConsumingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddAfterConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.AfterConsumingFilters.Add(typeof(T));
        return this;
    }
}
