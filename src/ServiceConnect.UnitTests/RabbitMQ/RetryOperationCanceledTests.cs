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

    [Fact]
    public async Task DoAsync_ExceptionActionThrowsOceUnderCancelledToken_PropagatesOce()
    {
        using var cts = new CancellationTokenSource();
        var initialFailure = new InvalidOperationException("first attempt failed");
        var oceFromCallback = new OperationCanceledException(cts.Token);

        var actionInvocations = 0;
        var callbackInvocations = 0;

        Task<bool> Action()
        {
            actionInvocations++;
            throw initialFailure;
        }

        Task ExceptionAction(Exception ex)
        {
            callbackInvocations++;
            cts.Cancel();
            throw oceFromCallback;
        }

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Retry.DoAsync(Action, ExceptionAction, retryInterval: TimeSpan.Zero, retryCount: 3, cancellationToken: cts.Token));

        Assert.Same(oceFromCallback, thrown);
        Assert.Equal(1, actionInvocations);
        Assert.Equal(1, callbackInvocations);
    }
}
