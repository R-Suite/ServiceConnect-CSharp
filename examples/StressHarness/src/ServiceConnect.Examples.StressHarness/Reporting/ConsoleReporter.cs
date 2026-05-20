using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Reporting;

/// <summary>
/// Thread-safe console sink for the orchestrator. Smoke and throughput modes call
/// <see cref="Heartbeat"/> and <see cref="FlowResult"/> to emit a line per flow.
/// Soak mode brackets the loop with <see cref="BeginSoakMode"/> /
/// <see cref="EndSoakMode"/>; while active, per-flow PASS lines are suppressed and
/// a single heartbeat line is emitted every <c>summaryInterval</c> with running
/// totals. Failures still print inline so an operator sees them in real time.
/// </summary>
public sealed class ConsoleReporter : IDisposable
{
    private readonly object _gate = new();
    private bool _soakMode;
    private Timer? _heartbeatTimer;
    private DateTime _soakStartedAt;
    private int _soakTickCount;
    private int _soakPassedCount;
    private int _soakFailedCount;

    public void BeginSoakMode(TimeSpan summaryInterval)
    {
        lock (_gate)
        {
            _soakMode = true;
            _soakStartedAt = DateTime.UtcNow;
            _soakTickCount = 0;
            _soakPassedCount = 0;
            _soakFailedCount = 0;
            _heartbeatTimer = new Timer(_ => EmitSoakHeartbeat(), null, summaryInterval, summaryInterval);
        }
    }

    public void EndSoakMode()
    {
        lock (_gate)
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _soakMode = false;
        }
    }

    public void Heartbeat(int tick, int totalTicks, string currentPattern)
    {
        lock (_gate)
        {
            if (_soakMode)
            {
                _soakTickCount = tick;
                return;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tick {tick}/{totalTicks}] running {currentPattern}"));
        }
    }

    public void FlowResult(string patternName, bool succeeded, TimeSpan elapsed, string? failure)
    {
        lock (_gate)
        {
            if (_soakMode)
            {
                if (succeeded)
                {
                    _soakPassedCount++;
                    return;
                }

                _soakFailedCount++;
                var failureSuffix = failure is null ? string.Empty : $" — {failure}";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [FAIL] {patternName} ({elapsed.TotalMilliseconds:F0}ms){failureSuffix}"));
                return;
            }

            var status = succeeded ? "PASS" : "FAIL";
            var suffix = failure is null ? string.Empty : $" — {failure}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  [{status}] {patternName} ({elapsed.TotalMilliseconds:F0}ms){suffix}"));
        }
    }

    private void EmitSoakHeartbeat()
    {
        lock (_gate)
        {
            if (!_soakMode)
            {
                return;
            }

            var elapsedSec = (int)(DateTime.UtcNow - _soakStartedAt).TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[soak +{elapsedSec}s] tick={_soakTickCount} passed={_soakPassedCount} failed={_soakFailedCount}"));
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

    public void Dispose()
    {
        lock (_gate)
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
    }
}
