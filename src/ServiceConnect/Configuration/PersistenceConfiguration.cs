using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPersistenceConfiguration"/> used to configure persistent ServiceConnect storage.
/// </summary>
public sealed class PersistenceConfiguration : IPersistenceConfiguration
{
    /// <remarks>WARNING: Default connects to localhost without authentication. Override in production.</remarks>
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    /// <inheritdoc />
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    /// <inheritdoc />
    public string AggregatorCollectionName { get; set; } = "Aggregator";
}
