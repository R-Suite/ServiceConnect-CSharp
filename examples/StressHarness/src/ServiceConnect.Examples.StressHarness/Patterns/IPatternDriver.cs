using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns;

public interface IPatternDriver
{
    string Name { get; }
    bool RequiresPersistence { get; }
    Task<FlowResult> RunFlowAsync(
        IBus sender,
        IBus receiver,
        StressFlowContext context,
        CancellationToken cancellationToken);
}
