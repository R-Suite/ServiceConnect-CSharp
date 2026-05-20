using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Post-chaos recovery check. Polls <see cref="IBus.IsConsuming"/> on each bus until both
/// report true or the supplied budget elapses; surfaces the offending bus(es) in the failure
/// message so the report rendering can attribute the regression. The 100 ms poll interval
/// matches the wall-clock granularity of the rest of the harness's recovery accounting and
/// keeps the worst-case overshoot of the budget bounded at one tick.
/// </summary>
public static class RecoveryAssertion
{
    public static async Task<AssertionOutcome> CheckBothBusesConsumingAsync(
        IBus alpha,
        IBus beta,
        TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        ArgumentNullException.ThrowIfNull(beta);

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (alpha.IsConsuming && beta.IsConsuming)
            {
                return AssertionOutcome.Pass();
            }
            await Task.Delay(100).ConfigureAwait(false);
        }

        var alphaState = alpha.IsConsuming ? "consuming" : "NOT consuming";
        var betaState = beta.IsConsuming ? "consuming" : "NOT consuming";
        return AssertionOutcome.Fail($"recovery budget exceeded: alpha={alphaState}, beta={betaState}");
    }
}
