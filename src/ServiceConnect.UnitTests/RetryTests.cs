using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RetryTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Do_ExecutesActionSuccessfully()
    {
        var executed = false;

        Retry.Do(() => { executed = true; }, _ => { }, FastInterval, 3);

        Assert.True(executed);
    }

    [Fact]
    public void Do_RetriesOnFailure()
    {
        int attempts = 0;

        Retry.Do(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("transient");
            },
            _ => { },
            FastInterval,
            3);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Do_ThrowsAggregateException_WhenAllRetriesFail()
    {
        var ex = Assert.Throws<AggregateException>(() =>
            Retry.Do(
                () => throw new InvalidOperationException("fail"),
                _ => { },
                FastInterval,
                3));

        Assert.Equal(3, ex.InnerExceptions.Count);
    }

    [Fact]
    public void Do_CallsExceptionActionOnEachFailure()
    {
        var exceptionsCaught = new List<Exception>();

        Assert.Throws<AggregateException>(() =>
            Retry.Do(
                () => throw new InvalidOperationException("fail"),
                ex => exceptionsCaught.Add(ex),
                FastInterval,
                3));

        Assert.Equal(3, exceptionsCaught.Count);
    }

    [Fact]
    public void DoGeneric_ReturnsValueOnSuccess()
    {
        var result = Retry.Do(() => 42, _ => { }, FastInterval, 3);

        Assert.Equal(42, result);
    }

    [Fact]
    public void DoGeneric_RetriesAndReturnsValue()
    {
        int attempts = 0;

        var result = Retry.Do(
            () =>
            {
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("transient");
                return attempts;
            },
            _ => { },
            FastInterval,
            3);

        Assert.Equal(2, result);
        Assert.Equal(2, attempts);
    }
}
