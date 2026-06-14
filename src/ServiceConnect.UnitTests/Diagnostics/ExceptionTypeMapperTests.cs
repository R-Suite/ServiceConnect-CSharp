using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.Diagnostics;

public class ExceptionTypeMapperTests
{
    [Fact]
    public void Map_OperationCanceledException_ReturnsCancelled()
    {
        Assert.Equal("cancelled", ExceptionTypeMapper.Map(new OperationCanceledException()));
    }

    [Fact]
    public void Map_TaskCanceledException_ReturnsCancelled()
    {
        // TaskCanceledException extends OperationCanceledException — subclass must also match.
        Assert.Equal("cancelled", ExceptionTypeMapper.Map(new TaskCanceledException()));
    }

    [Fact]
    public void Map_TimeoutException_ReturnsTimeout()
    {
        Assert.Equal("timeout", ExceptionTypeMapper.Map(new TimeoutException()));
    }

    [Fact]
    public void Map_UnrelatedExceptionFallsBackToTypeName()
    {
        Assert.Equal("InvalidOperationException", ExceptionTypeMapper.Map(new InvalidOperationException()));
    }

    [Fact]
    public void Map_AlreadyClosedException_ReturnsChannelClosed()
    {
        var ex = new AlreadyClosedException(
            new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "test"));
        Assert.Equal("channel_closed", ExceptionTypeMapper.Map(ex));
    }

    [Fact]
    public void Map_AlreadyClosedException_DoesNotReturnBrokerInterrupted()
    {
        // Confirms subclass ordering: AlreadyClosedException extends OperationInterruptedException
        // and must resolve to "channel_closed", not "broker_interrupted".
        var ex = new AlreadyClosedException(
            new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "test"));
        Assert.NotEqual("broker_interrupted", ExceptionTypeMapper.Map(ex));
    }

    [Fact]
    public void Map_OperationInterruptedException_ReturnsBrokerInterrupted()
    {
        var ex = new OperationInterruptedException(
            new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "test"));
        Assert.Equal("broker_interrupted", ExceptionTypeMapper.Map(ex));
    }

    [Fact]
    public void Map_BrokerUnreachableException_ReturnsBrokerUnreachable()
    {
        var ex = new BrokerUnreachableException(
            new InvalidOperationException("connect failed"));
        Assert.Equal("broker_unreachable", ExceptionTypeMapper.Map(ex));
    }

    [Fact]
    public void Map_PublishException_ReturnsPublishNacked()
    {
        var ex = new PublishException(1, false);
        Assert.Equal("publish_nacked", ExceptionTypeMapper.Map(ex));
    }

    [Fact]
    public void Map_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionTypeMapper.Map(null!));
    }
}
