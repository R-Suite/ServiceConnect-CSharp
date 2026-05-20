using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Cli;

public static class HarnessCliParser
{
    private static readonly string[] ValidModes = ["smoke", "soak", "throughput"];
    private static readonly string[] ValidPersistence = ["inmemory", "mongo"];
    private static readonly string[] ValidChaos = ["none", "docker"];

    public static HarnessCliOptions Parse(IReadOnlyList<string> args)
    {
        var opts = HarnessCliOptions.Defaults();

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            string Next()
            {
                if (++i >= args.Count)
                {
                    throw new ArgumentException($"missing value for {flag}");
                }

                return args[i];
            }

            opts = flag switch
            {
                "--mode" => opts with { Mode = Validate(Next(), ValidModes, flag) },
                "--duration" => opts with { Duration = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) },
                "--rate" => opts with { Rate = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--patterns" => opts with { Patterns = Next().Split(',', StringSplitOptions.RemoveEmptyEntries) },
                "--persistence" => opts with { Persistence = Validate(Next(), ValidPersistence, flag) },
                "--chaos" => opts with { Chaos = Validate(Next(), ValidChaos, flag) },
                "--broker" => opts with { BrokerUri = Next() },
                "--flow-timeout" => opts with { FlowTimeout = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) },
                "--memory-budget-mb" => opts with { MemoryBudgetBytes = long.Parse(Next(), CultureInfo.InvariantCulture) * 1024 * 1024 },
                "--report-dir" => opts with { ReportDir = Next() },
                "--chaos-interval" => opts with { ChaosInterval = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) },
                "--chaos-downtime" => opts with { ChaosDowntime = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) },
                "--chaos-recovery-budget" => opts with { ChaosRecoveryBudget = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"unknown flag {flag}"),
            };
        }

        return opts;
    }

    private static string Validate(string value, string[] allowed, string flagName)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException($"{flagName} must be one of: {string.Join(", ", allowed)}");
        }

        return value;
    }
}
