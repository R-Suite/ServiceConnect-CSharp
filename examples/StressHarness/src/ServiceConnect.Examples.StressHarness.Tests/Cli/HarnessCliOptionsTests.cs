using ServiceConnect.Examples.StressHarness.Cli;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Cli;

public class HarnessCliOptionsTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsDefaults()
    {
        var opts = HarnessCliParser.Parse([]);
        Assert.Equal("smoke", opts.Mode);
        Assert.Equal("inmemory", opts.Persistence);
        Assert.Equal("none", opts.Chaos);
        Assert.Equal("amqp://localhost", opts.BrokerUri);
    }

    [Fact]
    public void Parse_AllFlags_OverridesDefaults()
    {
        var args = new[]
        {
            "--mode", "soak",
            "--duration", "00:02:00",
            "--rate", "250",
            "--persistence", "mongo",
            "--broker", "amqp://broker.example",
            "--flow-timeout", "00:00:15",
            "--memory-budget-mb", "75",
            "--report-dir", "custom-out",
        };
        var opts = HarnessCliParser.Parse(args);
        Assert.Equal("soak", opts.Mode);
        Assert.Equal(TimeSpan.FromMinutes(2), opts.Duration);
        Assert.Equal(250, opts.Rate);
        Assert.Equal("mongo", opts.Persistence);
        Assert.Equal("amqp://broker.example", opts.BrokerUri);
        Assert.Equal(TimeSpan.FromSeconds(15), opts.FlowTimeout);
        Assert.Equal(75L * 1024 * 1024, opts.MemoryBudgetBytes);
        Assert.Equal("custom-out", opts.ReportDir);
    }

    [Fact]
    public void Parse_ChaosDocker_ThrowsInPhase1()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            HarnessCliParser.Parse(["--chaos", "docker"]));
        Assert.Contains("Phase 2", ex.Message);
    }

    [Fact]
    public void Parse_InvalidMode_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            HarnessCliParser.Parse(["--mode", "not-a-mode"]));
    }
}
