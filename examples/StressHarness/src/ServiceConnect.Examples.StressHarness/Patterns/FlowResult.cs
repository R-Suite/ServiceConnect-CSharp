namespace ServiceConnect.Examples.StressHarness.Patterns;

public sealed record FlowResult(
    bool Succeeded,
    TimeSpan Elapsed,
    int MessagesSent,
    int MessagesHandled,
    IReadOnlyList<string> AssertionFailures)
{
    public static FlowResult Pass(TimeSpan elapsed, int sent, int handled) =>
        new(true, elapsed, sent, handled, []);

    public static FlowResult Fail(TimeSpan elapsed, int sent, int handled, params string[] failures) =>
        new(false, elapsed, sent, handled, failures);
}
