using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Reporting;

public sealed class ConsoleReporter
{
    private readonly object _gate = new();

    public void Heartbeat(int tick, int totalTicks, string currentPattern)
    {
        lock (_gate)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tick {tick}/{totalTicks}] running {currentPattern}"));
        }
    }

    public void FlowResult(string patternName, bool succeeded, TimeSpan elapsed, string? failure)
    {
        lock (_gate)
        {
            var status = succeeded ? "PASS" : "FAIL";
            var failureSuffix = failure is null ? string.Empty : $" — {failure}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  [{status}] {patternName} ({elapsed.TotalMilliseconds:F0}ms){failureSuffix}"));
        }
    }

    public void Summary(Report report)
    {
        lock (_gate)
        {
            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Mode:     {report.Mode}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Duration: {report.Duration}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Flows:    {report.PassedFlows} passed, {report.FailedFlows} failed of {report.TotalFlows}"));
            Console.WriteLine($"Memory:   baseline {Bytes.Format(report.MemoryBaselineBytes)} → final {Bytes.Format(report.MemoryFinalBytes)}");

            var anyFailures = report.Patterns.Any(p => p.AssertionFailures.Count > 0)
                || report.ProcessAssertionFailures.Count > 0;
            if (!anyFailures)
            {
                Console.WriteLine("All assertions passed.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Assertion failures:");
            foreach (var p in report.Patterns)
            {
                foreach (var f in p.AssertionFailures)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{p.Name}] {f}"));
                }
            }

            foreach (var f in report.ProcessAssertionFailures)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [process] {f}"));
            }
        }
    }
}
