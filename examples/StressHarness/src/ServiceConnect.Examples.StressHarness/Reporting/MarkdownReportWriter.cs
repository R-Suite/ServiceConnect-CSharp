using System.Globalization;
using System.Text;

namespace ServiceConnect.Examples.StressHarness.Reporting;

public static class MarkdownReportWriter
{
    public static async Task WriteAsync(Report report, string filePath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Stress harness report");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Mode:** {report.Mode}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Started:** {report.StartedAtUtc:o}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Completed:** {report.CompletedAtUtc:o}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Duration:** {report.Duration}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Flows:** {report.PassedFlows} / {report.TotalFlows} passed"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Memory:** baseline {report.MemoryBaselineBytes:N0} → final {report.MemoryFinalBytes:N0} bytes"));
        sb.AppendLine();
        sb.AppendLine("## Per-pattern results");
        sb.AppendLine();
        sb.AppendLine("| Pattern | Runs | Passed | Failed | p50 (ms) | p95 (ms) | p99 (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in report.Patterns)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {p.Name} | {p.Runs} | {p.Passed} | {p.Failed} | {p.LatencyP50Ms:F1} | {p.LatencyP95Ms:F1} | {p.LatencyP99Ms:F1} |"));
        }

        var anyFailures = report.Patterns.Any(p => p.AssertionFailures.Count > 0)
            || report.ProcessAssertionFailures.Count > 0;
        if (anyFailures)
        {
            sb.AppendLine();
            sb.AppendLine("## Assertion failures");
            sb.AppendLine();
            foreach (var p in report.Patterns)
            {
                foreach (var fail in p.AssertionFailures)
                {
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **{p.Name}** — {fail}"));
                }
            }

            foreach (var fail in report.ProcessAssertionFailures)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **process** — {fail}"));
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
