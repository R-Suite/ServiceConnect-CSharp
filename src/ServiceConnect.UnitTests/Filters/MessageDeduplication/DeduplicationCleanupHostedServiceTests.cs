using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.UnitTests.Filters.MessageDeduplication;

public class DeduplicationCleanupHostedServiceTests
{
    private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

    [Fact]
    public async Task DisableMsgExpiryTrue_NeverCallsPersistor()
    {
        var svc = new DeduplicationCleanupHostedService(
            _persistor.Object,
            Microsoft.Extensions.Options.Options.Create(new DeduplicationFilterSettings
            {
                DisableMsgExpiry = true,
                MsgCleanupIntervalMinutes = 1
            }),
            NullLogger<DeduplicationCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        _persistor.Verify(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisableMsgExpiryFalse_CallsRemoveExpiredOnInterval()
    {
        var callCount = 0;
        _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref callCount);
                return Task.CompletedTask;
            });

        var svc = DeduplicationCleanupHostedService.CreateForTesting(
            _persistor.Object,
            disableMsgExpiry: false,
            interval: TimeSpan.FromMilliseconds(50),
            NullLogger<DeduplicationCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        await Task.Delay(250); // allow a few intervals

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        Assert.True(callCount >= 2, $"Expected >= 2 calls, got {callCount}");
    }

    [Fact]
    public async Task StoppingTokenCancelled_StopsGracefullyWithoutThrow()
    {
        _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = DeduplicationCleanupHostedService.CreateForTesting(
            _persistor.Object,
            disableMsgExpiry: false,
            interval: TimeSpan.FromMilliseconds(50),
            NullLogger<DeduplicationCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        // Should not throw
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PersistorThrows_LogsErrorAndContinuesLoop()
    {
        var callCount = 0;
        _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromException(new InvalidOperationException("boom"));
            });

        var svc = DeduplicationCleanupHostedService.CreateForTesting(
            _persistor.Object,
            disableMsgExpiry: false,
            interval: TimeSpan.FromMilliseconds(50),
            NullLogger<DeduplicationCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        await Task.Delay(250);

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        // Loop continued and retried despite exceptions.
        Assert.True(callCount >= 2, $"Expected >= 2 calls, got {callCount}");
    }
}
