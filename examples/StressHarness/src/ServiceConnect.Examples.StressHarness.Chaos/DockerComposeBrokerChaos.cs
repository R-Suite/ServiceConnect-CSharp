using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// <see cref="IBrokerChaos"/> implementation backed by <c>docker compose
/// stop</c> / <c>docker compose start</c>. The compose project is
/// addressed by its file path and project name so multiple harness runs
/// can target distinct stacks on the same host without colliding.
/// Network partition is intentionally not implemented; the surface is
/// reserved for a future iteration that can layer <c>tc</c>/<c>iptables</c>
/// or a userland TCP proxy on top of the same seam.
/// </summary>
/// <remarks>
/// <paramref name="stopTimeout"/> is forwarded to <c>docker compose stop</c>
/// as <c>-t &lt;seconds&gt;</c>. Docker's default SIGTERM-to-SIGKILL grace
/// of 10 s is too short for RabbitMQ to flush in-memory delivered-but-unacked
/// state plus its queue index at the throughput rates the chaos soak drives;
/// extending the grace lets the broker reach a clean shutdown rather than
/// being SIGKILL'd mid-flush, which would otherwise lose any messages still
/// in RAM. Integer seconds because both docker and RabbitMQ honour second
/// granularity for shutdown timeouts.
/// </remarks>
public sealed class DockerComposeBrokerChaos(string composeFile, string projectName, IProcessRunner runner, TimeSpan stopTimeout) : IBrokerChaos
{
    public Task KillNodeAsync(string nodeName, CancellationToken cancellationToken) =>
        RunComposeAsync("stop", nodeName, cancellationToken);

    public Task RestartNodeAsync(string nodeName, CancellationToken cancellationToken) =>
        RunComposeAsync("start", nodeName, cancellationToken);

    public Task PartitionAsync(string nodeName, TimeSpan duration, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "Network partition is out of scope for the first chaos build; use KillNodeAsync + RestartNodeAsync.");

    private async Task RunComposeAsync(string verb, string nodeName, CancellationToken cancellationToken)
    {
        // Only `stop` accepts `-t`; `start` would reject the flag. The stop
        // grace is forwarded as integer seconds (the only granularity docker
        // and RabbitMQ both honour) so the broker has enough time to flush
        // its queue index and unacked-delivery state to disk before SIGKILL.
        string[] args;
        if (string.Equals(verb, "stop", StringComparison.Ordinal))
        {
            var stopSeconds = ((int)stopTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            args = ["compose", "-f", composeFile, "-p", projectName, "stop", "-t", stopSeconds, nodeName];
        }
        else
        {
            args = ["compose", "-f", composeFile, "-p", projectName, verb, nodeName];
        }

        var exitCode = await runner.RunAsync("docker", args, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"docker compose {verb} {nodeName} exited {exitCode}"));
        }
    }
}
