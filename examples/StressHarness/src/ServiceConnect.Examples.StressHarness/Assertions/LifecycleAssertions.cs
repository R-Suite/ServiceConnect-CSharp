using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Post-flow lifecycle checks. Run from the smoke-mode orchestrator after the regular driver
/// loop completes; the bus passed to <see cref="DisposeDuringFlowAsync"/> is unusable after
/// the call returns, so the test must run last in the smoke sequence.
/// </summary>
/// <remarks>
/// The harness's <see cref="Orchestrator.HarnessHost.DisposeAsync"/> already swallows
/// per-step exceptions on teardown, so the subsequent <c>await using</c> dispose in
/// <c>Program.cs</c> tolerates a bus that was already disposed by this assertion without
/// surfacing a secondary failure.
/// </remarks>
public static class LifecycleAssertions
{
    [SuppressMessage("Style", "IDE0060", Justification = "Beta is the in-flight consumer; the parameter documents the dispatch target even though disposal targets alpha.")]
    public static async Task<AssertionOutcome> DisposeDuringFlowAsync(IBus alpha, IBus beta, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        ArgumentNullException.ThrowIfNull(beta);

        // Drop a small burst onto beta's queue while alpha is still alive so the dispose
        // path has genuinely in-flight outbound work to drain. The messages are routed to
        // beta's work queue (default-exchange endpoint) regardless of whether beta is
        // actively dispatching them when dispose fires — the test is about producer-side
        // graceful shutdown, not handler completion.
        for (var i = 0; i < 5; i++)
        {
            await alpha.SendAsync(
                new P2pPing(Guid.NewGuid()) { Token = "dispose-test" },
                new SendOptions { EndPoint = "stress-b.work" }).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await alpha.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AssertionOutcome.Fail(string.Create(CultureInfo.InvariantCulture,
                $"alpha.DisposeAsync threw: {ex.GetType().Name}: {ex.Message}"));
        }

        sw.Stop();
        if (sw.Elapsed > timeout)
        {
            return AssertionOutcome.Fail(string.Create(CultureInfo.InvariantCulture,
                $"alpha.DisposeAsync took {sw.Elapsed}, exceeded budget {timeout}"));
        }

        return AssertionOutcome.Pass();
    }
}
