using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Reporting;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Reporting;

public sealed class MessageLedgerReportTests
{
    [Fact]
    public async Task Markdown_renders_message_ledger_section_when_analysis_present()
    {
        var analysis = new MessageLedgerAnalysis(
            TotalPublishes: 100,
            AckedPublishes: 99,
            FailedPublishes: 1,
            TotalConsumes: 95,
            AckedAndConsumed: 95,
            AckedButLost: 4,
            FailedThenConsumed: 0,
            FailedAndLost: 1,
            PerMessageRedeliveries: 0,
            AckedButLostByWindow: new Dictionary<ChaosWindow, int> { [ChaosWindow.InRecovery] = 4 },
            AckedButLostByPattern: new Dictionary<string, int> { ["streaming"] = 3, ["aggregator"] = 1 },
            AckedButLostSample: [],
            ConsumesWithoutPublish: 0);

        var report = BuildReportFixture(messageLedger: analysis);
        var tempPath = Path.GetTempFileName();
        try
        {
            await MarkdownReportWriter.WriteAsync(report, tempPath, CancellationToken.None);
            var md = await File.ReadAllTextAsync(tempPath);

            Assert.Contains("## Message ledger", md);
            Assert.Contains("**Publishes:** 100 (acked 99 / failed 1)", md);
            Assert.Contains("**Acked-but-lost:** 4", md);
            Assert.Contains("| InRecovery | 4 |", md);
            Assert.Contains("| streaming | 3 |", md);
            Assert.Contains("| aggregator | 1 |", md);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task Markdown_skips_message_ledger_section_when_analysis_null()
    {
        var report = BuildReportFixture(messageLedger: null);
        var tempPath = Path.GetTempFileName();
        try
        {
            await MarkdownReportWriter.WriteAsync(report, tempPath, CancellationToken.None);
            var md = await File.ReadAllTextAsync(tempPath);
            Assert.DoesNotContain("## Message ledger", md);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task Markdown_renders_consumes_without_publish_warning_only_when_nonzero()
    {
        var withGap = new MessageLedgerAnalysis(
            TotalPublishes: 10,
            AckedPublishes: 10,
            FailedPublishes: 0,
            TotalConsumes: 12,
            AckedAndConsumed: 10,
            AckedButLost: 0,
            FailedThenConsumed: 0,
            FailedAndLost: 0,
            PerMessageRedeliveries: 0,
            AckedButLostByWindow: new Dictionary<ChaosWindow, int>(),
            AckedButLostByPattern: new Dictionary<string, int>(),
            AckedButLostSample: [],
            ConsumesWithoutPublish: 2);

        var report = BuildReportFixture(messageLedger: withGap);
        var tempPath = Path.GetTempFileName();
        try
        {
            await MarkdownReportWriter.WriteAsync(report, tempPath, CancellationToken.None);
            var md = await File.ReadAllTextAsync(tempPath);
            Assert.Contains("**Consumes without matching publish:** 2", md);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static Report BuildReportFixture(MessageLedgerAnalysis? messageLedger)
    {
        return new Report(
            ReportVersion: 3,
            Mode: "smoke",
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            MemoryBaselineBytes: 0,
            MemoryFinalBytes: 0,
            TotalFlows: 0,
            PassedFlows: 0,
            FailedFlows: 0,
            Patterns: [],
            ProcessAssertionFailures: [],
            Metadata: new ReportMetadata("h", "rt", "amqp://x", "inmemory"),
            Chaos: null,
            MessageLedger: messageLedger);
    }
}
