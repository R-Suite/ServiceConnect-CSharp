namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Contract for injecting broker-side faults — node kills, restarts, and
/// network partitions — into a stress run. The default implementation is
/// <see cref="NoopBrokerChaos"/>: chaos is opt-in and the harness ships
/// without any failover behaviour wired up by default. Concrete chaos
/// implementations (for example <see cref="DockerComposeBrokerChaos"/>)
/// translate these abstract operations into the underlying orchestration
/// commands (docker compose stop/start, iptables, etc.) that produce the
/// requested broker state.
/// </summary>
public interface IBrokerChaos
{
    Task KillNodeAsync(string nodeName, CancellationToken cancellationToken);

    Task RestartNodeAsync(string nodeName, CancellationToken cancellationToken);

    Task PartitionAsync(string nodeName, TimeSpan duration, CancellationToken cancellationToken);
}
