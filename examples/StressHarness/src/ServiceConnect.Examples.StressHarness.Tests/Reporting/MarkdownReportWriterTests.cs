using ServiceConnect.Examples.StressHarness.Reporting;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Reporting;

public class MarkdownReportWriterTests
{
    [Fact]
    public async Task WriteAsync_IncludesHeadlineCountsAndPatternRows()
    {
        var report = new Report(
            ReportVersion: 2,
            Mode: "smoke",
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            CompletedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(30),
            Duration: TimeSpan.FromSeconds(30),
            MemoryBaselineBytes: 800_000,
            MemoryFinalBytes: 900_000,
            TotalFlows: 28,
            PassedFlows: 27,
            FailedFlows: 1,
            Patterns:
            [
                new PatternStats("p2p", 2, 2, 0, 1, 0, 1, 0, 5.0, 10.0, 15.0, [], []),
                new PatternStats("pubsub", 2, 1, 1, 1, 0, 0, 1, 8.0, 12.0, 20.0,
                    ["α→β handler fired 1 time, expected 2"],
                    [new FailedFlowDetail(
                        FlowId: Guid.Parse("12345678-1234-1234-1234-123456789abc"),
                        Direction: "α→β",
                        Failures: ["handler fired 1 time, expected 2"])]),
            ],
            ProcessAssertionFailures: []);

        var tempDir = Path.Combine(Path.GetTempPath(), $"stress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "report.md");

        try
        {
            await MarkdownReportWriter.WriteAsync(report, path, CancellationToken.None);
            var md = await File.ReadAllTextAsync(path);
            Assert.Contains("**Mode:** smoke", md);
            Assert.Contains("27 / 28", md);
            Assert.Contains("| p2p | 2 | 2/0 | 1/0 | 1/0 | ", md);
            Assert.Contains("| pubsub | 2 | 1/1 | 1/0 | 0/1 | ", md);
            Assert.Contains("α→β handler fired 1 time, expected 2", md);
            Assert.Contains("KB", md);
            Assert.Contains("## Failed flows", md);
            Assert.Contains("### pubsub", md);
            Assert.Contains("**α→β** `12345678-1234-1234-1234-123456789abc`", md);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
