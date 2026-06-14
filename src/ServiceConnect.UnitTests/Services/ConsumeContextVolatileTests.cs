using System;
using System.Collections.Generic;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Pins the cache-stability semantics introduced by the volatile double-checked-publication
/// pattern on <see cref="ConsumeContext.MessageId"/> and <see cref="ConsumeContext.CorrelationId"/>.
/// The value resolved on first read must be the value returned forever after; the volatile
/// flag's release/acquire semantics make the payload write visible to all subsequent readers.
/// </summary>
public class ConsumeContextVolatileTests
{
    private static ConsumeContext BuildContext(IDictionary<string, object> headers)
    {
        var bus = new Mock<IBus>().Object;
        IQueueConfiguration queueConfig = new QueueConfiguration
        {
            QueueName = "test-queue",
            ErrorQueueName = "test-errors",
            AuditQueueName = "test-audit"
        };
        IBusConfiguration busConfig = new BusConfiguration();
        return new ConsumeContext(bus, headers, queueConfig, busConfig);
    }

    [Fact]
    public void MessageId_RepeatedReads_ReturnSameCachedValue()
    {
        var headers = new Dictionary<string, object>
        {
            { HeaderKeys.MessageId, Guid.NewGuid().ToString() }
        };
        var ctx = BuildContext(headers);

        var first = ctx.MessageId;
        var second = ctx.MessageId;

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CorrelationId_RepeatedReads_ReturnSameCachedValue()
    {
        var correlationGuid = Guid.NewGuid();
        var headers = new Dictionary<string, object>
        {
            { HeaderKeys.CorrelationId, correlationGuid.ToString() }
        };
        var ctx = BuildContext(headers);

        var first = ctx.CorrelationId;
        var second = ctx.CorrelationId;

        Assert.Equal(correlationGuid, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CorrelationId_AbsentHeader_ReturnsGuidEmpty()
    {
        // _correlationId is a Guid with Guid.Empty as the sentinel for "absent header".
        // Modelling it as Guid? with a null sentinel would also produce Guid.Empty here,
        // so this test pins the absent-header outcome regardless of the underlying field type.
        var headers = new Dictionary<string, object>();
        var ctx = BuildContext(headers);

        Assert.Equal(Guid.Empty, ctx.CorrelationId);
    }
}
