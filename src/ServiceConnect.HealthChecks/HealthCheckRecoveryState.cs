namespace ServiceConnect.HealthChecks;

/// <summary>
/// Mutable per-bus / per-consumer state used by the recovery-grace window. Lives
/// outside the IHealthCheck instance so it survives the per-probe re-allocation
/// that the framework's HealthCheckService imposes — each probe runs against a
/// fresh IServiceScope, so the scope's IServiceProvider is a different key in
/// <see cref="PerProviderCache{T}"/> on every probe and the cache produces a fresh
/// check instance each time. Without an external state object, an instance-scoped
/// LastHealthyTicks would reset to 0 every probe and the grace window would never
/// trigger.
/// </summary>
internal sealed class HealthCheckRecoveryState
{
    /// <summary>
    /// UTC ticks of the most recent Healthy observation. Zero means never-observed-Healthy
    /// since this state instance was created. Use <see cref="System.Threading.Volatile"/>
    /// reads/writes for cross-thread visibility.
    /// </summary>
    public long LastHealthyTicks;
}
