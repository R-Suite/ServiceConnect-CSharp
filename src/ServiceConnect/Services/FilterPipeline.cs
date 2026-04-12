using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class FilterPipeline(IPipelineConfiguration config, IServiceProvider serviceProvider) : IFilterPipeline
{
    public bool ExecuteOutgoingFilters(Envelope envelope)
    {
        return ExecuteFilters(config.OutgoingFilters, envelope);
    }

    public bool ExecuteBeforeConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(config.BeforeConsumingFilters, envelope);
    }

    public bool ExecuteAfterConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(config.AfterConsumingFilters, envelope);
    }

    private bool ExecuteFilters(IList<Type> filterTypes, Envelope envelope)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        foreach (Type filterType in filterTypes)
        {
            var filter = (IFilter)serviceProvider.GetRequiredService(filterType);

            bool continueProcessing = filter.Process(envelope);
            if (!continueProcessing)
                return true; // stopped
        }

        return false; // not stopped
    }
}
