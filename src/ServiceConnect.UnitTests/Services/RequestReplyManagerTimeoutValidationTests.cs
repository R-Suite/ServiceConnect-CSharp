using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class RequestReplyManagerTimeoutValidationTests
{
    private static RequestReplyManager CreateManager()
    {
        var mockSerializer = new Mock<IMessageSerializer>(MockBehavior.Loose);
        var mockSendPipeline = new Mock<ISendMessagePipeline>(MockBehavior.Loose);
        return new RequestReplyManager(mockSerializer.Object, mockSendPipeline.Object, new BusConfiguration());
    }

    [Fact]
    public async Task SendRequestAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        var manager = CreateManager();
        var options = new RequestOptions { Timeout = -5 };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                options,
                CancellationToken.None));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task SendRequestMultiAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        var manager = CreateManager();
        var options = new RequestOptions { Timeout = -5 };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                options,
                CancellationToken.None));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task PublishRequestAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        var manager = CreateManager();
        var options = new RequestOptions { Timeout = -5 };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                options,
                _ => { },
                CancellationToken.None));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task SendRequestAsync_TimeoutInfinite_DoesNotThrowValidationException()
    {
        var manager = CreateManager();
        var options = new RequestOptions { Timeout = Timeout.Infinite };

        // Validation must not throw for Timeout.Infinite. The request will
        // pend indefinitely against the loose mocks, so cancel via CTS.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                options,
                cts.Token));
    }

    [Fact]
    public async Task SendRequestAsync_ZeroTimeout_ThrowsWithGuidanceTowardDefault()
    {
        // default(RequestOptions) skips the parameterless ctor and leaves Timeout=0;
        // ValidateOptions must reject it so callers get a clear error instead of
        // an immediate RequestTimeoutException after 0ms.
        var manager = CreateManager();

#pragma warning disable IDE0034 // explicit form documents the default(T) trap intentionally
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                default(RequestOptions),
                CancellationToken.None));
#pragma warning restore IDE0034

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("RequestOptions.Default", ex.Message);
    }
}
