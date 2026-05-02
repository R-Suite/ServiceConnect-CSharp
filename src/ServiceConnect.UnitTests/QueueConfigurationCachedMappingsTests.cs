using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class QueueConfigurationCachedMappingsTests
{
    [Fact]
    public void QueueMappings_TwoConsecutiveAccesses_ReturnSameReference()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");

        var first = config.QueueMappings;
        var second = config.QueueMappings;

        // Pre-fix: each access allocates a new QueueMappingsView wrapper → references differ.
        // Post-fix: the wrapper is cached → same reference.
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
}
