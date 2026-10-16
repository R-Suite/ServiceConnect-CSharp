using System;
using Xunit;

namespace ServiceConnect.IntegrationTestsSsl
{
    /// <summary>
    /// A test that needs a TLS MongoDB server. It is skipped unless <c>SERVICECONNECT_MONGODB_SSL</c> holds a
    /// ServiceConnect SSL connection string; <c>scripts/start-test-mongo.sh</c> starts a server and prints one.
    /// </summary>
    public sealed class MongoDbSslFactAttribute : FactAttribute
    {
        public const string ConnectionStringVariable = "SERVICECONNECT_MONGODB_SSL";

        public MongoDbSslFactAttribute()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                Skip = $"Set {ConnectionStringVariable} to run the MongoDB SSL integration tests.";
            }
        }

        public static string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringVariable);
    }
}
