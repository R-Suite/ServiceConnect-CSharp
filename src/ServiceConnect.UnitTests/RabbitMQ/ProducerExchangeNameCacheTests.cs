using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that the Producer exchange-name cache is keyed on FullName, not
/// AssemblyQualifiedName. Keying on AQN causes duplicate cache entries when
/// assembly version or type forwarding changes AQN while FullName stays the
/// same, because the cached value is derived from FullName only.
/// </summary>
public class ProducerExchangeNameCacheTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    [Fact]
    public void GetExchangeName_PopulatesCacheKeyedOnFullNameNotAssemblyQualifiedName()
    {
        var producer = CreateProducer();

        var method = typeof(Producer).GetMethod("GetExchangeName",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(producer, [typeof(string)]);

        var cacheField = typeof(Producer).GetField("_exchangeNameCache",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (ConcurrentDictionary<string, string>)cacheField.GetValue(producer)!;

        Assert.True(cache.ContainsKey(typeof(string).FullName!),
            "Cache key must be FullName.");
        Assert.False(cache.ContainsKey(typeof(string).AssemblyQualifiedName!),
            "Cache key must NOT be AssemblyQualifiedName.");
    }
}
