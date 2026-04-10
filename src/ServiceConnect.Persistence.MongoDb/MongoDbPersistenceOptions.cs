namespace ServiceConnect.Persistence.MongoDb;

public class MongoDbPersistenceOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistantStore";
    public MongoDbSslOptions? Ssl { get; set; }
}
