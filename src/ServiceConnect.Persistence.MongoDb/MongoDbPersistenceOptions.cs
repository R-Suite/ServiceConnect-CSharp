namespace ServiceConnect.Persistence.MongoDb;

public sealed class MongoDbPersistenceOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    public MongoDbSslOptions? Ssl { get; set; }
}
