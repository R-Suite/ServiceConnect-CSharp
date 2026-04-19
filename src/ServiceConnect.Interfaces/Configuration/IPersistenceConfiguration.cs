namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Configures persistence storage used by process managers and aggregators.
/// </summary>
public interface IPersistenceConfiguration
{
    /// <summary>
    /// Gets or sets the provider-specific connection string.
    /// </summary>
    string ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the database or logical store name.
    /// </summary>
    string DatabaseName { get; set; }

    /// <summary>
    /// Gets or sets the collection or container name used for aggregator state.
    /// </summary>
    string AggregatorCollectionName { get; set; }
}
