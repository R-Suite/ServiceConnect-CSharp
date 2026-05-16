using Microsoft.Extensions.Logging;
using Moq;
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
}
