using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class BusHostedServiceTests
{
    private readonly Mock<IBus> _mockBus = new();
    private readonly Mock<IBusConfiguration> _mockConfig = new();
    private readonly Mock<ITransportConfiguration> _mockTransport = new();
    private readonly Mock<ILogger<BusHostedService>> _mockLogger = new();

    private ILogger<BusHostedService> Logger => _mockLogger.Object;

    private BusHostedService CreateSut()
    {
        // Default transport: TLS on against loopback so no plaintext warning fires in these tests.
        _mockTransport.SetupGet(t => t.SslEnabled).Returns(true);
        _mockTransport.SetupGet(t => t.Host).Returns("localhost");
        _mockTransport.SetupGet(t => t.SuppressPlaintextWarning).Returns(false);
        // These tests exercise the auto-start / replay / shutdown branches of BusHostedService
        // without injecting a real IProducer mock; opt out of the producer presence check so
        // those branches are reachable. Producer-presence is covered by
        // BusHostedServiceMissingProducerTests in the BusInterface namespace.
        _mockConfig.SetupGet(c => c.AllowMissingProducer).Returns(true);
        return new(_mockBus.Object, _mockConfig.Object, _mockTransport.Object, Logger);
    }

    [Fact]
    public async Task StartAsync_ValidateReplyDestinationsDisabled_LogsWarning()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(false);
        _mockConfig.Setup(c => c.ValidateReplyDestinations).Returns(false);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ValidateReplyDestinations is disabled")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_ValidateReplyDestinationsEnabled_DoesNotLogWarning()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(false);
        _mockConfig.Setup(c => c.ValidateReplyDestinations).Returns(true);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ValidateReplyDestinations is disabled")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_AutoStartTrue_CallsStartConsuming()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(true);
        _mockConfig.Setup(c => c.ValidateReplyDestinations).Returns(true);
        _mockBus.Setup(b => b.StartConsumingAsync()).Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StartConsumingAsync(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_AutoStartFalse_DoesNotCallStartConsuming()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(false);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StartConsumingAsync(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NoConsumerRegistered_PropagatesException()
    {
        // The hosted service must surface startup failures to the host rather
        // than log a warning and silently report success.
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(true);
        _mockBus.Setup(b => b.StartConsumingAsync())
            .ThrowsAsync(new InvalidOperationException("No consumer registered."));

        var sut = CreateSut();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_CallsStopConsumingAsync()
    {
        _mockBus.Setup(b => b.StopConsumingAsync()).Returns(Task.CompletedTask);
        var sut = CreateSut();
        await sut.StopAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StopConsumingAsync(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_AfterStop_ThrowsInvalidOperationException()
    {
        // The bus permanently latches _stopped after StopConsumingAsync. A second
        // StartAsync on the same instance must propagate the InvalidOperationException
        // that IBus.StartConsumingAsync throws rather than swallowing it.
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(true);
        _mockConfig.Setup(c => c.ValidateReplyDestinations).Returns(true);
        _mockBus.Setup(b => b.StartConsumingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockBus.Setup(b => b.StopConsumingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        // Simulate the latched-stopped state: a fresh StartConsumingAsync call after stop throws.
        _mockBus.Setup(b => b.StartConsumingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Bus has been stopped and cannot be restarted."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_ConsumerHangs_LogsAndReturnsAfterGracefulShutdownTimeout()
    {
        // A non-cooperative IConsumer swallows the cancellation and never completes.
        // StopAsync must respect GracefulShutdownTimeoutMilliseconds and return once
        // the grace window elapses, logging a warning rather than blocking indefinitely.
        var transport = new TransportConfiguration { GracefulShutdownTimeoutMilliseconds = 250 };
        transport.Freeze();

        CancellationToken capturedCt = default;
        var hangingBus = new Mock<IBus>();
        hangingBus.Setup(b => b.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken ct) => capturedCt = ct)
            .Returns(async (CancellationToken ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* swallow to mimic non-cooperative consumer */ }
            });

        var mockLogger = new Mock<ILogger<BusHostedService>>();
        var svc = new BusHostedService(hangingBus.Object, _mockConfig.Object, transport, mockLogger.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.StopAsync(CancellationToken.None);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 200, 1500);
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("GracefulShutdownTimeout", StringComparison.OrdinalIgnoreCase)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        // After the grace window, the linked CTS passed to the mock should have been cancelled.
        Assert.True(capturedCt.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAsync_OuterCtCancelled_DoesNotLogGraceWarning()
    {
        // When the host's outer CT fires (container kill, operator Ctrl+C), StopAsync must
        // not emit a "grace exceeded" warning — the cancellation is external, not timeout.
        var transport = new TransportConfiguration { GracefulShutdownTimeoutMilliseconds = 5000 };
        transport.Freeze();

        var hangingBus = new Mock<IBus>();
        hangingBus.Setup(b => b.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            });

        var mockLogger = new Mock<ILogger<BusHostedService>>();
        var svc = new BusHostedService(hangingBus.Object, _mockConfig.Object, transport, mockLogger.Object);

        using var outerCts = new CancellationTokenSource(200); // host kill-CT fires after 200ms; grace is 5s
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.StopAsync(outerCts.Token));

        // No "grace exceeded" warning should be logged — the cancellation is the outer host CT, not grace.
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("GracefulShutdownTimeout", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task StopAsync_GraceMsZero_DelegatesDirectlyToBusWithoutTimeout()
    {
        // GracefulShutdownTimeoutMilliseconds=0 bypasses the WhenAny race entirely;
        // the bus token is the host's outer CT with no linked grace CTS.
        var transport = new TransportConfiguration { GracefulShutdownTimeoutMilliseconds = 0 };
        transport.Freeze();

        var cooperativeBus = new Mock<IBus>();
        cooperativeBus.Setup(b => b.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockLogger = new Mock<ILogger<BusHostedService>>();
        var svc = new BusHostedService(cooperativeBus.Object, _mockConfig.Object, transport, mockLogger.Object);

        await svc.StopAsync(CancellationToken.None);

        cooperativeBus.Verify(b => b.StopConsumingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_CooperativeBusInsideGraceWindow_AwaitsWithoutWarning()
    {
        // A cooperative bus that finishes within the grace window must not emit a warning
        // and must not take longer than the grace window.
        var transport = new TransportConfiguration { GracefulShutdownTimeoutMilliseconds = 5000 };
        transport.Freeze();

        var cooperativeBus = new Mock<IBus>();
        cooperativeBus.Setup(b => b.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.Delay(50));

        var mockLogger = new Mock<ILogger<BusHostedService>>();
        var svc = new BusHostedService(cooperativeBus.Object, _mockConfig.Object, transport, mockLogger.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.StopAsync(CancellationToken.None);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 30, 500);
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
