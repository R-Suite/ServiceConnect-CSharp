using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class FilterPipeline(IPipelineConfiguration config, IServiceProvider serviceProvider) : IFilterPipeline
{
    public Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.OutgoingFilters, envelope, cancellationToken);
    }

    public Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.BeforeConsumingFilters, envelope, cancellationToken);
    }

    public Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.AfterConsumingFilters, envelope, cancellationToken);
    }

    private async Task<bool> ExecuteFiltersAsync(IList<Type> filterTypes, Envelope envelope, CancellationToken cancellationToken)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        foreach (Type filterType in filterTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = (IFilter)serviceProvider.GetRequiredService(filterType);

            bool continueProcessing = await filter.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!continueProcessing)
                return true; // stopped
        }

        return false; // not stopped
    }
}
