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
public sealed class DockerComposeBrokerChaos(string composeFile, string projectName, IProcessRunner runner) : IBrokerChaos
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
        string[] args = ["compose", "-f", composeFile, "-p", projectName, verb, nodeName];
        var exitCode = await runner.RunAsync("docker", args, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"docker compose {verb} {nodeName} exited {exitCode}"));
        }
    }
}
