namespace ServiceConnect.Interfaces.Configuration;

public interface IPersistenceConfiguration
{
    string ConnectionString { get; set; }
    string DatabaseName { get; set; }
    string AggregatorCollectionName { get; set; }
}
