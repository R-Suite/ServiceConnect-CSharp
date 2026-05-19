namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Placeholder for the docker-compose-driven chaos implementation: kills,
/// restarts, and partitions translate to <c>docker compose stop</c> /
/// <c>start</c> / network-isolation commands against the cluster compose
/// file. Every method throws <see cref="NotImplementedException"/> until
/// the failover harness lands; the CLI currently rejects <c>--chaos
/// docker</c> so this type is unreachable in a normal run.
/// </summary>
public sealed class DockerComposeBrokerChaos : IBrokerChaos
{
    private const string Reason = "DockerComposeBrokerChaos is a future-phase stub; the cluster-failover plan implements the docker compose stop/start invocations.";

    public Task KillNodeAsync(string nodeName, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Reason);

    public Task RestartNodeAsync(string nodeName, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Reason);

    public Task PartitionAsync(string nodeName, TimeSpan duration, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Reason);
}
