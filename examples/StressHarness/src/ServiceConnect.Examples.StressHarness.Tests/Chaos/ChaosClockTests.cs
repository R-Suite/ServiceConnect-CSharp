using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Chaos;

public class ChaosClockTests
{
    [Fact]
    public void InitialWindow_IsPreChaos()
    {
        var clock = new ChaosClock();
        Assert.Equal(ChaosWindow.PreChaos, clock.CurrentWindow);
    }

    [Fact]
    public void SetWindow_UpdatesCurrentWindow()
    {
        var clock = new ChaosClock();

        clock.SetWindow(ChaosWindow.DuringChaos);
        Assert.Equal(ChaosWindow.DuringChaos, clock.CurrentWindow);

        clock.SetWindow(ChaosWindow.InRecovery);
        Assert.Equal(ChaosWindow.InRecovery, clock.CurrentWindow);

        clock.SetWindow(ChaosWindow.PostChaos);
        Assert.Equal(ChaosWindow.PostChaos, clock.CurrentWindow);
    }
}
