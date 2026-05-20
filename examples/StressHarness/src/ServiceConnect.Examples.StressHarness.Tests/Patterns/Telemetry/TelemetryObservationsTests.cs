using System.Diagnostics;
using ServiceConnect.Examples.StressHarness.Patterns.Telemetry;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Telemetry;

public class TelemetryObservationsTests
{
    [Fact]
    public void TryRemoveCompleted_DropsCompletedFlowActivities()
    {
        using var obs = new TelemetryObservations();
        using var source = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName);

        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();

        Emit(source, keep);
        Emit(source, drop);

        Assert.NotEmpty(obs.GetActivitiesFor(keep));
        Assert.NotEmpty(obs.GetActivitiesFor(drop));

        obs.TryRemoveCompleted([drop]);

        Assert.NotEmpty(obs.GetActivitiesFor(keep));
        Assert.Empty(obs.GetActivitiesFor(drop));
    }

    // The framework stamps the conversation-id tag as Guid.ToString() (default
    // "D" format). The listener's parser accepts both "D" and "N" so callers
    // that pre-format with "N" still index correctly; emitting "D" here mirrors
    // what the production code actually puts on the wire.
    private static void Emit(ActivitySource source, Guid flowId)
    {
        using var activity = source.StartActivity("test");
        activity?.SetTag(MessagingAttributes.MessageConversationId, flowId.ToString());
    }
}
