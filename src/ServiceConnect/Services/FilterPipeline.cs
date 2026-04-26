using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Resolves and executes configured filters for outgoing and incoming message envelopes.
/// Filters are resolved per call from <see cref="ConsumeScopeAccessor.Current"/> so that
/// scoped dependencies honour the same message scope as the dispatcher and handlers.
/// </summary>
public sealed class FilterPipeline(IPipelineConfiguration config, ConsumeScopeAccessor scopeAccessor) : IFilterPipeline
{
    /// <inheritdoc />
    public Task<FilterAction> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.OutgoingFilters, envelope, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FilterAction> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.BeforeConsumingFilters, envelope, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FilterAction> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.AfterConsumingFilters, envelope, cancellationToken);
    }

    private async Task<FilterAction> ExecuteFiltersAsync(IReadOnlyList<Type> filterTypes, Envelope envelope, CancellationToken cancellationToken)
    {
        if (filterTypes == null || filterTypes.Count == 0)
        {
            return FilterAction.Continue;
        }

        var serviceProvider = scopeAccessor.Current;

        foreach (Type filterType in filterTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = (IFilter)serviceProvider.GetRequiredService(filterType);

            FilterAction action = await filter.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (action == FilterAction.Stop)
            {
                return FilterAction.Stop;
            }
        }

        return FilterAction.Continue;
    }
}
