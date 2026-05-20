using System.Text.Json;
using ServiceConnect.Examples.StressHarness.Reporting;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Reporting;

public class JsonReportWriterTests
{
    [Fact]
    public async Task WriteAsync_ProducesParseableJsonWithReportVersion()
    {
        var report = new Report(
            ReportVersion: 3,
            Mode: "smoke",
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            CompletedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(30),
            Duration: TimeSpan.FromSeconds(30),
            MemoryBaselineBytes: 100_000_000,
            MemoryFinalBytes: 105_000_000,
            TotalFlows: 28,
            PassedFlows: 28,
            FailedFlows: 0,
            Patterns:
            [
                new PatternStats("p2p", 2, 2, 0, 1, 0, 1, 0, 5.0, 10.0, 15.0, [], []),
            ],
            ProcessAssertionFailures: [],
            Metadata: new ReportMetadata("test-host", "net10.0", "amqp://localhost", "inmemory"),
            Chaos: null,
            MessageLedger: null);

        var tempDir = Path.Combine(Path.GetTempPath(), $"stress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "report.json");

        try
        {
            await JsonReportWriter.WriteAsync(report, path, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(3, doc.RootElement.GetProperty("reportVersion").GetInt32());
            Assert.Equal("smoke", doc.RootElement.GetProperty("mode").GetString());
            Assert.Equal(28, doc.RootElement.GetProperty("totalFlows").GetInt32());
            Assert.Equal("p2p", doc.RootElement.GetProperty("patterns")[0].GetProperty("name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("patterns")[0].GetProperty("alphaPassed").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("patterns")[0].GetProperty("betaPassed").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("patterns")[0].GetProperty("failedFlows").GetArrayLength());
            Assert.Equal("test-host", doc.RootElement.GetProperty("metadata").GetProperty("hostname").GetString());
            Assert.Equal("inmemory", doc.RootElement.GetProperty("metadata").GetProperty("persistenceMode").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
