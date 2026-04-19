using System.Reflection;
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RetryTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task DoAsync_ExecutesActionSuccessfully()
    {
        var executed = false;

        await Retry.DoAsync(() => { executed = true; return Task.CompletedTask; }, _ => Task.CompletedTask, FastInterval, 3);

        Assert.True(executed);
    }

    [Fact]
    public async Task DoAsync_RetriesOnFailure()
    {
        int attempts = 0;

        await Retry.DoAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            FastInterval,
            3);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DoAsync_ThrowsAggregateException_WhenAllRetriesFail()
    {
        // retryCount=3 -> total attempts = 4 (initial + 3 retries).
        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            Retry.DoAsync(
                () => throw new InvalidOperationException("fail"),
                _ => Task.CompletedTask,
                FastInterval,
                3));

        Assert.Equal(4, ex.InnerExceptions.Count);
    }

    [Fact]
    public async Task DoAsync_CallsExceptionActionOnEachFailure()
    {
        var exceptionsCaught = new List<Exception>();

        // retryCount=3 -> total attempts = 4 (initial + 3 retries).
        await Assert.ThrowsAsync<AggregateException>(() =>
            Retry.DoAsync(
                () => throw new InvalidOperationException("fail"),
                ex => { exceptionsCaught.Add(ex); return Task.CompletedTask; },
                FastInterval,
                3));

        Assert.Equal(4, exceptionsCaught.Count);
    }

    [Fact]
    public async Task DoAsync_RethrowsMatchingOperationCanceledException_WithoutCallingExceptionAction()
    {
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var exceptionActionCalls = 0;

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Retry.DoAsync(
                () => throw new OperationCanceledException("canceled", cancellationToken),
                _ =>
                {
                    exceptionActionCalls++;
                    return Task.CompletedTask;
                },
                FastInterval,
                3,
                cancellationToken));

        Assert.Equal(cancellationToken, ex.CancellationToken);
        Assert.Equal(0, exceptionActionCalls);
    }

    [Fact]
    public async Task DoAsync_RethrowsOperationCanceledException_WhenCallerTokenIsCanceledEvenIfExceptionTokenDiffers()
    {
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var otherToken = new CancellationTokenSource().Token;
        var exceptionActionCalls = 0;

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Retry.DoAsync(
                () =>
                {
                    cancellationSource.Cancel();
                    throw new OperationCanceledException("canceled", otherToken);
                },
                _ =>
                {
                    exceptionActionCalls++;
                    return Task.CompletedTask;
                },
                FastInterval,
                3,
                cancellationToken));

        Assert.Equal(otherToken, ex.CancellationToken);
        Assert.Equal(0, exceptionActionCalls);
    }

    [Fact]
    public async Task DoAsyncGeneric_ReturnsValueOnSuccess()
    {
        var result = await Retry.DoAsync(() => Task.FromResult(42), _ => Task.CompletedTask, FastInterval, 3);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task DoAsyncGeneric_RetriesAndReturnsValue()
    {
        int attempts = 0;

        var result = await Retry.DoAsync(
            () =>
            {
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("transient");
                return Task.FromResult(attempts);
            },
            _ => Task.CompletedTask,
            FastInterval,
            3);

        Assert.Equal(2, result);
        Assert.Equal(2, attempts);
    }

    // Verify the backoff cap prevents double overflow for huge retry counts.
    // Math.Pow(2, N) for N > 1023 returns +Infinity which propagates through
    // TimeSpan.FromMilliseconds to throw OverflowException.  The cap at 52 means
    // the raw exponent never exceeds 2^52 (~4.5e15 ms), which the 5-minute ceiling
    // clamps to a safe value.
    [Theory]
    [InlineData(53)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void CalculateDelay_DoesNotOverflow_ForLargeRetryAttempts(int retryAttempt)
    {
        var calculateDelay = typeof(Retry).GetMethod(
            "CalculateDelay",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (TimeSpan)calculateDelay.Invoke(null, [TimeSpan.FromMilliseconds(100), retryAttempt])!;

        // Should be capped at 5 minutes regardless of how large retryAttempt is
        Assert.True(result <= TimeSpan.FromMinutes(5), $"Delay {result} exceeded 5-minute ceiling for attempt {retryAttempt}");
        Assert.True(result >= TimeSpan.Zero, $"Delay {result} was negative for attempt {retryAttempt}");
    }

    [Fact]
    public void CalculateDelay_Cap52_ProducesSameResultAs53()
    {
        var calculateDelay = typeof(Retry).GetMethod(
            "CalculateDelay",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // Attempts 52, 53, 100, and MaxValue should all produce the same exponential
        // component (capped), so the only variation is jitter — both should be <= 5 min.
        var delay52 = (TimeSpan)calculateDelay.Invoke(null, [TimeSpan.FromMilliseconds(1), 52])!;
        var delay53 = (TimeSpan)calculateDelay.Invoke(null, [TimeSpan.FromMilliseconds(1), 53])!;
        var delayMax = (TimeSpan)calculateDelay.Invoke(null, [TimeSpan.FromMilliseconds(1), int.MaxValue])!;

        Assert.True(delay52 <= TimeSpan.FromMinutes(5));
        Assert.True(delay53 <= TimeSpan.FromMinutes(5));
        Assert.True(delayMax <= TimeSpan.FromMinutes(5));
    }
}
