using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageBusWriteStreamTimeProviderTests
{
    [Fact]
    public async Task CloseAsync_DrainTimeoutHonoursInjectedTimeProvider()
    {
        var stalledProducer = new Mock<IProducer>();
        var tcs = new TaskCompletionSource<bool>();

        // Block SendBytesAsync so _inFlightWrites stays above zero indefinitely,
        // forcing the drain loop in CloseAsync to spin until the deadline fires.
        stalledProducer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<byte[]>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(tcs.Task.ContinueWith(_ => { }));

        var fakeTime = new FakeTimeProvider();

        var stream = new MessageBusWriteStream(
            stalledProducer.Object, "endpoint-x", typeof(byte[]), fakeTime);

        // Fire a write that will never complete, keeping _inFlightWrites > 0.
        _ = stream.WriteAsync([1, 2, 3], 0, 3);

        // Start the drain without awaiting: the deadline is captured from fakeTime
        // (T+30s). The drain loop will spin until it yields, then do Task.Delay(10ms).
        var closeTask = stream.CloseAsync(CancellationToken.None);

        // Give the spin loop time to capture the deadline and start its first
        // Task.Delay(10ms), so advancing the clock afterwards puts us past the deadline
        // on the next spin iteration.
        await Task.Delay(50);
        fakeTime.Advance(TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<TimeoutException>(() => closeTask);

        // Release the stalled write so the background task can terminate cleanly.
        tcs.SetResult(true);
    }
}
