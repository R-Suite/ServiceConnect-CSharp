namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Phase of the chaos cycle, observed by flow runners so individual
/// directions can be tagged with the window they completed inside.
/// </summary>
public enum ChaosWindow
{
    PreChaos,
    DuringChaos,
    InRecovery,
    PostChaos,
}

/// <summary>
/// Thread-safe holder for the current <see cref="ChaosWindow"/>. The
/// scheduler writes the window as the kill/recovery cycle advances;
/// flow runners read it on each completion. The underlying int is
/// accessed via <see cref="Volatile"/> so writes from the scheduler's
/// background task are visible to reader threads without taking a lock.
/// </summary>
public sealed class ChaosClock
{
    private int _window = (int)ChaosWindow.PreChaos;

    public ChaosWindow CurrentWindow => (ChaosWindow)Volatile.Read(ref _window);

    public void SetWindow(ChaosWindow window) =>
        Volatile.Write(ref _window, (int)window);
}
