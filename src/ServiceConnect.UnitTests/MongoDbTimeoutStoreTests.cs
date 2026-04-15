using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreTests
{
    [Fact]
    public void BuildDueTimeoutFilter_IncludesExpiredLeasesForRecovery()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);

        var filter = MongoDbTimeoutStore.BuildDueTimeoutFilter(now);
        var rendered = filter.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry);

        var json = rendered.ToJson();

        Assert.Contains("\"Time\"", json);
        Assert.Contains("\"$or\"", json);
        Assert.Contains("\"Locked\" : false", json);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$lte\"", json);
    }
}
