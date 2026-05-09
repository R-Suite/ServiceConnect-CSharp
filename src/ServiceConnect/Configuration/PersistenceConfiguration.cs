using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPersistenceConfiguration"/> used to configure
/// persistent ServiceConnect storage.
/// </summary>
/// <remarks>
/// As of v8 (Phase 5), <see cref="ConnectionString"/> defaults to <see cref="string.Empty"/>;
/// callers must explicitly configure it. The pre-v8 default (<c>"mongodb://localhost/"</c>)
/// silently targeted localhost when misconfigured — production deployments shipping with the
/// default value were a real accident-mode. Legacy callers that depended on the default must
/// update their <c>ConfigurePersistence(c =&gt; c.ConnectionString = "...")</c> wiring.
/// </remarks>
public sealed class PersistenceConfiguration : IPersistenceConfiguration
{
    /// <summary>
    /// Provider-specific connection string. Required; no default. Misconfiguration
    /// surfaces as <see cref="System.InvalidOperationException"/> at first persistence
    /// use rather than silently targeting localhost.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <inheritdoc />
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    /// <inheritdoc />
    public string AggregatorCollectionName { get; set; } = "Aggregator";
}
