using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Chaos;

public class DockerComposeBrokerChaosTests
{
    [Fact]
    public async Task KillNodeAsync_InvokesDockerComposeStop_WithTimeout()
    {
        var runner = new RecordingProcessRunner();
        var chaos = new DockerComposeBrokerChaos(
            composeFile: "docker-compose.yml",
            projectName: "stress-harness",
            runner: runner,
            stopTimeout: TimeSpan.FromSeconds(30));

        await chaos.KillNodeAsync("rabbitmq", CancellationToken.None);

        Assert.Equal("docker", runner.LastFileName);
        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "-p", "stress-harness", "stop", "-t", "30", "rabbitmq"],
            runner.LastArguments);
    }

    [Fact]
    public async Task KillNodeAsync_CustomTimeout_ForwardsIntegerSeconds()
    {
        var runner = new RecordingProcessRunner();
        var chaos = new DockerComposeBrokerChaos(
            composeFile: "docker-compose.yml",
            projectName: "stress-harness",
            runner: runner,
            stopTimeout: TimeSpan.FromSeconds(90));

        await chaos.KillNodeAsync("rabbitmq", CancellationToken.None);

        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "-p", "stress-harness", "stop", "-t", "90", "rabbitmq"],
            runner.LastArguments);
    }

    [Fact]
    public async Task RestartNodeAsync_InvokesDockerComposeStart_WithoutTimeoutFlag()
    {
        var runner = new RecordingProcessRunner();
        var chaos = new DockerComposeBrokerChaos(
            composeFile: "docker-compose.yml",
            projectName: "stress-harness",
            runner: runner,
            stopTimeout: TimeSpan.FromSeconds(30));

        await chaos.RestartNodeAsync("rabbitmq", CancellationToken.None);

        // `docker compose start` does not accept `-t`; the timeout flag belongs
        // only to the stop path.
        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "-p", "stress-harness", "start", "rabbitmq"],
            runner.LastArguments);
    }

    [Fact]
    public async Task KillNodeAsync_NonZeroExitCode_Throws()
    {
        var runner = new RecordingProcessRunner { NextExitCode = 1 };
        var chaos = new DockerComposeBrokerChaos(
            composeFile: "docker-compose.yml",
            projectName: "stress-harness",
            runner: runner,
            stopTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chaos.KillNodeAsync("rabbitmq", CancellationToken.None));
    }

    [Fact]
    public async Task PartitionAsync_ThrowsNotImplementedException()
    {
        var chaos = new DockerComposeBrokerChaos(
            composeFile: "docker-compose.yml",
            projectName: "stress-harness",
            runner: new RecordingProcessRunner(),
            stopTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<NotImplementedException>(() =>
            chaos.PartitionAsync("rabbitmq", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public int NextExitCode { get; set; }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            LastFileName = fileName;
            LastArguments = [.. arguments];
            return Task.FromResult(NextExitCode);
        }
    }
}
