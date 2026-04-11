using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public class FilterPipeline : IFilterPipeline
{
    private readonly IPipelineConfiguration _config;
    private readonly IServiceProvider _serviceProvider;

    public FilterPipeline(IPipelineConfiguration config, IServiceProvider serviceProvider)
    {
        _config = config;
        _serviceProvider = serviceProvider;
    }

    public bool ExecuteOutgoingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.OutgoingFilters, envelope);
    }

    public bool ExecuteBeforeConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.BeforeConsumingFilters, envelope);
    }

    public bool ExecuteAfterConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.AfterConsumingFilters, envelope);
    }

    private bool ExecuteFilters(IList<Type> filterTypes, Envelope envelope)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        foreach (Type filterType in filterTypes)
        {
            var filter = (IFilter)_serviceProvider.GetRequiredService(filterType);

            bool continueProcessing = filter.Process(envelope);
            if (!continueProcessing)
                return true; // stopped
        }

        return false; // not stopped
    }
}
