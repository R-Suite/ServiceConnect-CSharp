using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class PersistenceConfiguration : IPersistenceConfiguration
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistantStore";
    public string AggregatorCollectionName { get; set; } = "Aggregator";
}
