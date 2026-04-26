using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class RetryOperationCanceledTests
{
    [Fact]
    public async Task DoAsync_UnrelatedOperationCanceled_PropagatesAfterFirstAttempt()
    {
        var attempts = 0;
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Retry.DoAsync(
                action: () =>
                {
                    attempts++;
                    return Task.FromException(new OperationCanceledException(unrelatedCts.Token));
                },
                exceptionAction: _ => Task.CompletedTask,
                retryInterval: TimeSpan.FromMilliseconds(1),
                retryCount: 4,
                cancellationToken: CancellationToken.None);
        });

        Assert.Equal(1, attempts);
    }
}
