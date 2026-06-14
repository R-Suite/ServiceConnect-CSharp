using System.Globalization;
using System.Linq;
using System.Text;
using ServiceConnect.Examples.StressHarness.Assertions;

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
        sb.AppendLine($"**Host:** {report.Metadata.Hostname}");
        sb.AppendLine($"**Runtime:** {report.Metadata.RuntimeVersion}");
        sb.AppendLine($"**Broker:** {report.Metadata.BrokerUri}");
        sb.AppendLine($"**Persistence:** {report.Metadata.PersistenceMode}");
        sb.AppendLine();
        sb.AppendLine($"**Mode:** {report.Mode}");
        sb.AppendLine($"**Started:** {report.StartedAtUtc.ToString("o", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"**Completed:** {report.CompletedAtUtc.ToString("o", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"**Duration:** {report.Duration}");
        sb.AppendLine($"**Flows:** {report.PassedFlows} / {report.TotalFlows} passed");
        sb.AppendLine($"**Memory:** baseline {Bytes.Format(report.MemoryBaselineBytes)} → final {Bytes.Format(report.MemoryFinalBytes)}");
        sb.AppendLine();
        sb.AppendLine("## Per-pattern results");
        sb.AppendLine();
        sb.AppendLine("| Pattern | Runs | Total P/F | α P/F | β P/F | p50 (ms) | p95 (ms) | p99 (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var p in report.Patterns)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {p.Name} | {p.Runs} | {p.Passed}/{p.Failed} | {p.AlphaPassed}/{p.AlphaFailed} | {p.BetaPassed}/{p.BetaFailed} | {p.LatencyP50Ms:F1} | {p.LatencyP95Ms:F1} | {p.LatencyP99Ms:F1} |"));
        }

        var anyFailures = report.Patterns.Any(p => p.AssertionFailures.Count > 0) || report.ProcessAssertionFailures.Count > 0;
        if (anyFailures)
        {
            sb.AppendLine();
            sb.AppendLine("## Assertion failures");
            sb.AppendLine();
            foreach (var p in report.Patterns)
            {
                foreach (var fail in p.AssertionFailures)
                {
                    sb.AppendLine($"- **{p.Name}** — {fail}");
                }
            }
            foreach (var fail in report.ProcessAssertionFailures)
            {
                sb.AppendLine($"- **process** — {fail}");
            }
        }

        var anyFailedFlows = report.Patterns.Any(p => p.FailedFlows.Count > 0);
        if (anyFailedFlows)
        {
            sb.AppendLine();
            sb.AppendLine("## Failed flows");
            foreach (var p in report.Patterns.Where(p => p.FailedFlows.Count > 0))
            {
                sb.AppendLine();
                sb.AppendLine($"### {p.Name}");
                foreach (var f in p.FailedFlows)
                {
                    var causes = string.Join("; ", f.Failures);
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"- **{f.Direction}** `{f.FlowId:D}` — {causes}"));
                }
            }
        }

        // Chaos sections render only when the soak's chaos scheduler was active.
        // The block contains two tables: the kill-event timeline (one row per
        // kill / restart pair the scheduler executed) and the per-pattern
        // breakdown of direction outcomes by ChaosWindow. Both are soft signal —
        // the hard chaos assertion is the bus-recovery check that already
        // populates ProcessAssertionFailures earlier in the soak.
        if (report.Chaos is not null)
        {
            var chaos = report.Chaos;
            sb.AppendLine();
            sb.AppendLine("## Chaos events");
            sb.AppendLine();
            sb.AppendLine($"**Kill events:** {chaos.KillEventCount}");
            sb.AppendLine($"**Duplicate handler invocations:** {chaos.DuplicateHandlerInvocations} (extra handler firings beyond expected — broker redelivery during chaos is expected; pre/post-chaos duplicates would be a real finding)");
            sb.AppendLine();
            sb.AppendLine("| Killed | Restarted | Node |");
            sb.AppendLine("|---|---|---|");
            foreach (var e in chaos.Events)
            {
                sb.AppendLine($"| {e.KilledAt.ToString("o", CultureInfo.InvariantCulture)} | {e.RestartedAt.ToString("o", CultureInfo.InvariantCulture)} | {e.NodeName} |");
            }

            sb.AppendLine();
            sb.AppendLine("## Per-pattern chaos window breakdown");
            sb.AppendLine();
            sb.AppendLine("| Pattern | Pre | During | InRecovery | Post |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in chaos.PerPattern)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {p.PatternName} | {p.PreChaosCount} | {p.DuringChaosCount} | {p.InRecoveryCount} | {p.PostChaosCount} |"));
            }
        }

        if (report.MessageLedger is not null)
        {
            var l = report.MessageLedger;
            sb.AppendLine();
            sb.AppendLine("## Message ledger");
            sb.AppendLine();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Publishes:** {l.TotalPublishes} (acked {l.AckedPublishes} / failed {l.FailedPublishes})"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Consumes:** {l.TotalConsumes}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Acked-but-lost:** {l.AckedButLost}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Failed-and-lost:** {l.FailedAndLost}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Per-message redeliveries:** {l.PerMessageRedeliveries}"));
            if (l.ConsumesWithoutPublish > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"**Consumes without matching publish:** {l.ConsumesWithoutPublish} (instrumentation gap — see spec risks)"));
            }

            if (l.AckedButLost > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Acked-but-lost breakdown by publish window");
                sb.AppendLine();
                sb.AppendLine("| Window | Count |");
                sb.AppendLine("|---|---|");
                foreach (var kv in l.AckedButLostByWindow.OrderBy(kv => kv.Key))
                {
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {kv.Key} | {kv.Value} |"));
                }

                sb.AppendLine();
                sb.AppendLine("### Acked-but-lost breakdown by pattern");
                sb.AppendLine();
                sb.AppendLine("| Pattern | Count |");
                sb.AppendLine("|---|---|");
                foreach (var kv in l.AckedButLostByPattern.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {kv.Key} | {kv.Value} |"));
                }

                if (l.AckedButLostSample.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"### Acked-but-lost — first {l.AckedButLostSample.Count} forensic rows"));
                    sb.AppendLine();
                    sb.AppendLine("| MessageId | FlowId | Pattern | OriginBus | PublishStarted | PublishWindow |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var row in l.AckedButLostSample)
                    {
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| {row.MessageId:N} | {row.FlowId:N} | {row.Pattern} | {row.OriginBus} | {row.PublishStarted.ToString("o", CultureInfo.InvariantCulture)} | {row.Window} |"));
                    }
                }
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
