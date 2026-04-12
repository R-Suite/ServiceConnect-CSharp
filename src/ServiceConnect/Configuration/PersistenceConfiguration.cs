using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class PersistenceConfiguration : IPersistenceConfiguration
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    public string AggregatorCollectionName { get; set; } = "Aggregator";
}
