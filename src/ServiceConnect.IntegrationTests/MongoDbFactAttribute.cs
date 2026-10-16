using System;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    /// <summary>
    /// A test that needs a MongoDB server. It is skipped unless <c>SERVICECONNECT_MONGODB</c> holds a
    /// MongoDB connection string; <c>scripts/start-test-mongo.sh</c> starts a server and prints one.
    /// </summary>
    public sealed class MongoDbFactAttribute : FactAttribute
    {
        public const string ConnectionStringVariable = "SERVICECONNECT_MONGODB";

        public MongoDbFactAttribute()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                Skip = $"Set {ConnectionStringVariable} to run the MongoDB integration tests.";
            }
        }

        public static string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringVariable);
    }
}
