using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Reply half of the stress harness's scatter-gather pattern. Each subscriber bus
/// constructs one of these from the inbound <see cref="SearchRequest.CorrelationId"/>
/// so the framework's request-reply manager can match the reply back to the
/// originating <see cref="ServiceConnect.Interfaces.IBus.PublishRequestAsync"/>
/// callback. <see cref="CatalogName"/> identifies which bus produced the reply so
/// the driver can assert the publish fanout reached both subscribers.
/// </summary>
public sealed class SearchResponse(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Identifier set by the handler to its bus tag (<c>"alpha"</c> / <c>"beta"</c>)
    /// so the driver can verify the publish reached both buses by checking the
    /// distinct values across the collected reply set.
    /// </summary>
    public string CatalogName { get; init; } = string.Empty;

    /// <summary>
    /// Per-reply payload echo, fixed at handler-construction so the driver can
    /// assert the reply-content path is intact alongside the catalog-name check.
    /// </summary>
    public string ResultId { get; init; } = string.Empty;
}
