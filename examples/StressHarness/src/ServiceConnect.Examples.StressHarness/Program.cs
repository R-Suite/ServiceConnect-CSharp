using ServiceConnect.Examples.StressHarness.Cli;

try
{
    var opts = HarnessCliParser.Parse(args);
    Console.WriteLine($"[stress-harness] mode={opts.Mode} persistence={opts.Persistence} chaos={opts.Chaos}");
    Console.WriteLine($"[stress-harness] broker={opts.BrokerUri} duration={opts.Duration} reportDir={opts.ReportDir}");
    // ModeDispatcher wires up in Task 12
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (NotSupportedException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
