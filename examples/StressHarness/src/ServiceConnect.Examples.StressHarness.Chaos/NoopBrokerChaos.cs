namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Default <see cref="IBrokerChaos"/> registration: every operation
/// completes immediately without touching the broker. Wired in by
/// <c>Program.cs</c> as the singleton implementation so the CLI's
/// <c>--chaos none</c> path (the only accepted value today) resolves
/// to a no-op without forcing every call-site to null-check the chaos
/// dependency.
/// </summary>
public sealed class NoopBrokerChaos : IBrokerChaos
{
    public Task KillNodeAsync(string nodeName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RestartNodeAsync(string nodeName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PartitionAsync(string nodeName, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
}
