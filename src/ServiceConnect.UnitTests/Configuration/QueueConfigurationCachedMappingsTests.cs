using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public sealed class QueueConfigurationCachedMappingsTests
{
    [Fact]
    public void QueueMappings_TwoConsecutiveAccesses_ReturnSameReference()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");

        var first = config.QueueMappings;
        var second = config.QueueMappings;

        // The QueueMappingsView wrapper is cached, so repeated access without mutation
        // returns the same reference instead of allocating per call.
        Assert.Same(first, second);
    }

    [Fact]
    public void QueueMappings_AfterMutation_ReturnsFreshWrapper()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");
        var first = config.QueueMappings;

        config.AddQueueMapping(typeof(int), "queue-2");
        var second = config.QueueMappings;

        // After AddQueueMapping mutates _queueMappings, the cached wrapper is invalidated and a
        // fresh one is allocated. References differ; both reflect the current mappings.
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void QueueMappings_AfterListOverloadMutation_ReturnsFreshWrapper()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");
        var first = config.QueueMappings;

        // The list overload also nulls _mappingsView; a regression that removes that
        // invalidation would leave first and second as the same reference, and second
        // would reflect only one key instead of two.
        config.AddQueueMapping(typeof(int), ["queue-2", "queue-3"]);
        var second = config.QueueMappings;

        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }
}
