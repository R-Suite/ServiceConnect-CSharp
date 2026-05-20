using Moq;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public class RecoveryAssertionTests
{
    [Fact]
    public async Task BothBusesConsuming_ReturnsPass()
    {
        var alpha = new Mock<IBus>(); alpha.SetupGet(b => b.IsConsuming).Returns(true);
        var beta = new Mock<IBus>(); beta.SetupGet(b => b.IsConsuming).Returns(true);

        var outcome = await RecoveryAssertion.CheckBothBusesConsumingAsync(
            alpha.Object, beta.Object, TimeSpan.FromMilliseconds(200));

        Assert.True(outcome.Ok);
    }

    [Fact]
    public async Task AlphaNotConsuming_AfterBudget_ReturnsFail()
    {
        var alpha = new Mock<IBus>(); alpha.SetupGet(b => b.IsConsuming).Returns(false);
        var beta = new Mock<IBus>(); beta.SetupGet(b => b.IsConsuming).Returns(true);

        var outcome = await RecoveryAssertion.CheckBothBusesConsumingAsync(
            alpha.Object, beta.Object, TimeSpan.FromMilliseconds(200));

        Assert.False(outcome.Ok);
        Assert.Contains("alpha", outcome.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BetaRecoversMidBudget_ReturnsPass()
    {
        var alpha = new Mock<IBus>(); alpha.SetupGet(b => b.IsConsuming).Returns(true);
        var betaCallCount = 0;
        var beta = new Mock<IBus>();
        beta.SetupGet(b => b.IsConsuming).Returns(() =>
        {
            betaCallCount++;
            return betaCallCount > 1;
        });

        var outcome = await RecoveryAssertion.CheckBothBusesConsumingAsync(
            alpha.Object, beta.Object, TimeSpan.FromSeconds(2));

        Assert.True(outcome.Ok);
    }
}
