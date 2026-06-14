using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

public sealed class MongoDbPersistenceOptionsAggregatorLeaseTests
{
    [Fact]
    public void AggregatorLeaseDuration_DefaultIsFiveMinutes()
    {
        var options = new MongoDbPersistenceOptions();
        Assert.Equal(System.TimeSpan.FromMinutes(5), options.AggregatorLeaseDuration);
    }

    [Fact]
    public void AggregatorLeaseDuration_IsSettable()
    {
        var options = new MongoDbPersistenceOptions
        {
            AggregatorLeaseDuration = System.TimeSpan.FromSeconds(30),
        };
        Assert.Equal(System.TimeSpan.FromSeconds(30), options.AggregatorLeaseDuration);
    }
}
