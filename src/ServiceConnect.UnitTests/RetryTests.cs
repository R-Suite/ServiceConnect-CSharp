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
        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            Retry.DoAsync(
                () => throw new InvalidOperationException("fail"),
                _ => Task.CompletedTask,
                FastInterval,
                3));

        Assert.Equal(3, ex.InnerExceptions.Count);
    }

    [Fact]
    public async Task DoAsync_CallsExceptionActionOnEachFailure()
    {
        var exceptionsCaught = new List<Exception>();

        await Assert.ThrowsAsync<AggregateException>(() =>
            Retry.DoAsync(
                () => throw new InvalidOperationException("fail"),
                ex => { exceptionsCaught.Add(ex); return Task.CompletedTask; },
                FastInterval,
                3));

        Assert.Equal(3, exceptionsCaught.Count);
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
}
