using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Verifies that the in-flight request cap previously hard-coded as
/// <c>RequestReplyManager.MaxInflightRequests = 10_000</c> is now sourced from
/// <see cref="ServiceConnect.Interfaces.Configuration.IBusConfiguration.MaxInflightRequests"/>.
/// The cap check is <c>_pendingRequests.Count &gt;= _maxInflightRequests</c>, so a configured
/// value of <c>1</c> rejects the second concurrent request while the first is still in flight.
/// </summary>
public class RequestReplyManagerMaxInflightConfigurableTests
{
    [Fact]
    public void BusConfiguration_MaxInflightRequests_DefaultsTo10_000()
    {
        var config = new BusConfiguration();

        Assert.Equal(10_000, config.MaxInflightRequests);
    }

    [Fact]
    public async Task SendRequestAsync_AtConfiguredCap_ThrowsAndQuotesConfiguredValue()
    {
        // Send pipeline parks on a Task.Delay tied to a test-owned CTS — never on the
        // inner linkedCts of SendRequestAsync, which is uncancellable when Timeout.Infinite
        // is combined with CancellationToken.None. The first request occupies the only
        // allowed slot; the second hits the cap check before allocating a RequestState.
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        using var pipelineCts = new CancellationTokenSource();
        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(),
                It.IsAny<CancellationToken>()))
            .Returns<SendContext, CancellationToken>((_, _) => Task.Delay(Timeout.Infinite, pipelineCts.Token));

        var config = new BusConfiguration { MaxInflightRequests = 1 };
        var manager = new RequestReplyManager(serializer.Object, pipeline.Object, config);

        var firstOptions = new RequestOptions { Timeout = Timeout.Infinite };
        var first = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            firstOptions,
            CancellationToken.None);

        // Yield so the first registration completes before the cap-check call.
        await Task.Delay(50);

        var secondOptions = new RequestOptions { Timeout = 5_000 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                secondOptions,
                CancellationToken.None));

        // Message must quote the configured cap (1), not the legacy default (10_000).
        Assert.Contains("(1)", ex.Message);
        Assert.DoesNotContain("10000", ex.Message);
        Assert.DoesNotContain("10_000", ex.Message);

        // Tidy up the pending first request. Cancelling the pipeline CTS surfaces an OCE
        // through the pipeline mock; SendRequestAsync propagates that as a faulted Task.
        pipelineCts.Cancel();
        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task SendRequestMultiAsync_AtConfiguredCap_ThrowsAndQuotesConfiguredValue()
    {
        // Same shape as the SendRequestAsync test but exercising the multi-reply branch.
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        using var pipelineCts = new CancellationTokenSource();
        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(),
                It.IsAny<CancellationToken>()))
            .Returns<SendContext, CancellationToken>((_, _) => Task.Delay(Timeout.Infinite, pipelineCts.Token));

        var config = new BusConfiguration { MaxInflightRequests = 2 };
        var manager = new RequestReplyManager(serializer.Object, pipeline.Object, config);

        var infiniteOptions = new RequestOptions { Timeout = Timeout.Infinite };
        var first = manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            infiniteOptions,
            CancellationToken.None);
        var second = manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            infiniteOptions,
            CancellationToken.None);

        // Yield so both pending entries register before the third call.
        await Task.Delay(50);

        var thirdOptions = new RequestOptions { Timeout = 5_000 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                thirdOptions,
                CancellationToken.None));

        Assert.Contains("(2)", ex.Message);
        Assert.DoesNotContain("10000", ex.Message);
        Assert.DoesNotContain("10_000", ex.Message);

        pipelineCts.Cancel();
        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
    }

    [Fact]
    public void Constructor_ThrowsWhenBusConfigurationIsNull()
    {
        var serializer = new Mock<IMessageSerializer>(MockBehavior.Loose);
        var pipeline = new Mock<ISendMessagePipeline>(MockBehavior.Loose);

        Assert.Throws<ArgumentNullException>(() =>
            new RequestReplyManager(serializer.Object, pipeline.Object, null!));
    }
}
