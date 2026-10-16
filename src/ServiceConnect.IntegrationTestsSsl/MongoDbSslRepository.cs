using MongoDB.Driver;
using ServiceConnect.Persistance.MongoDbSsl;

namespace ServiceConnect.IntegrationTestsSsl
{
    public class MongoDbSslRepository
    {
        public IMongoDatabase MongoDatabase { get; }

        public MongoDbSslRepository(string connectionString, string databaseName)
        {
            MongoDatabase = MongoDbSslConnection.GetDatabase(connectionString, databaseName);
        }
    }
}
