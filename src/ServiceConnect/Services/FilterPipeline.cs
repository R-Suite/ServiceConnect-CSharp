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
    /// <summary>
    /// Runs the configured outgoing filters and returns whether processing was stopped.
    /// </summary>
    public Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.OutgoingFilters, envelope, cancellationToken);
    }

    /// <summary>
    /// Runs the configured pre-consume filters and returns whether processing was stopped.
    /// </summary>
    public Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.BeforeConsumingFilters, envelope, cancellationToken);
    }

    /// <summary>
    /// Runs the configured post-consume filters and returns whether processing was stopped.
    /// </summary>
    public Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.AfterConsumingFilters, envelope, cancellationToken);
    }

    private async Task<bool> ExecuteFiltersAsync(IReadOnlyList<Type> filterTypes, Envelope envelope, CancellationToken cancellationToken)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        var serviceProvider = scopeAccessor.Current;

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
