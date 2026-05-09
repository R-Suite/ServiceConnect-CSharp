using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Pins the contract that OutboundHeaderBuilder is the sole authoritative stamper of
/// MessageType on the wire.  The value is the operation name ("Publish"|"Send"|"ByteStream"),
/// not a CLR type name.  Type identity is carried by TypeName / FullTypeName.
/// </summary>
public sealed class OutboundHeaderBuilderOperationNameTests
{
    private static OutboundHeaderBuilder CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        return new OutboundHeaderBuilder(
            busConfig.Object,
            queueConfig.Object,
            new FakeTimeProvider(),
            NullLogger.Instance);
    }

    [Theory]
    [InlineData("Publish")]
    [InlineData("Send")]
    [InlineData("ByteStream")]
    public void BuildHeaders_StampsOperationNameInMessageType(string operation)
    {
        var headers = CreateBuilder().BuildHeaders(typeof(string), null, "queue", operation);

        // Contract: MessageType is the operation name on the wire. The builder is
        // the sole stamper; Bus does not write MessageType into the envelope.
        Assert.Equal(operation, headers[HeaderKeys.MessageType]);
        Assert.Equal(typeof(string).FullName, headers[HeaderKeys.TypeName]);
        Assert.Equal(typeof(string).AssemblyQualifiedName, headers[HeaderKeys.FullTypeName]);
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesMessageType_OverwrittenByOperationName()
    {
        // Even if the Bus were to pass MessageType in the header dictionary,
        // the builder overwrites it with the authoritative operation name.
        var caller = new Dictionary<string, string>
        {
            [HeaderKeys.MessageType] = "caller.spoof",
        };

        var headers = CreateBuilder().BuildHeaders(typeof(string), caller, "queue", "Publish");

        Assert.Equal("Publish", headers[HeaderKeys.MessageType]);
    }
}
