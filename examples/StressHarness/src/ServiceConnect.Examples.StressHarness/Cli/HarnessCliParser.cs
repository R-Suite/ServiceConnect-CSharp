using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Cli;

public static class HarnessCliParser
{
    private static readonly string[] ValidModes = ["smoke", "soak", "throughput"];
    private static readonly string[] ValidPersistence = ["inmemory", "mongo"];
    private static readonly string[] ValidChaos = ["none"];

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

            switch (flag)
            {
                case "--mode":
                    opts = opts with { Mode = Validate(Next(), ValidModes, flag) };
                    break;
                case "--duration":
                    opts = opts with { Duration = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--rate":
                    opts = opts with { Rate = int.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--patterns":
                    opts = opts with { Patterns = Next().Split(',', StringSplitOptions.RemoveEmptyEntries) };
                    break;
                case "--persistence":
                    opts = opts with { Persistence = Validate(Next(), ValidPersistence, flag) };
                    break;
                case "--chaos":
                    var chaos = Next();
                    if (chaos == "docker")
                    {
                        throw new NotSupportedException(
                            "--chaos docker is not yet implemented; pass --chaos none.");
                    }

                    opts = opts with { Chaos = Validate(chaos, ValidChaos, flag) };
                    break;
                case "--broker":
                    opts = opts with { BrokerUri = Next() };
                    break;
                case "--flow-timeout":
                    opts = opts with { FlowTimeout = TimeSpan.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--memory-budget-mb":
                    opts = opts with { MemoryBudgetBytes = long.Parse(Next(), CultureInfo.InvariantCulture) * 1024 * 1024 };
                    break;
                case "--report-dir":
                    opts = opts with { ReportDir = Next() };
                    break;
                default:
                    throw new ArgumentException($"unknown flag {flag}");
            }
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
