using ServiceConnect.Examples.StressHarness.Assertions;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public class FlowAccountingTests
{
    [Fact]
    public void RecordSendThenHandle_ReconcilesAsHandled()
    {
        var acct = new FlowAccounting();
        var flowId = Guid.NewGuid();

        acct.RecordSend(flowId, expectedHandlerInvocations: 1);
        acct.RecordHandled(flowId);

        var summary = acct.Reconcile();
        Assert.Equal(1, summary.SentCount);
        Assert.Equal(1, summary.HandledCount);
        Assert.Empty(summary.MissingFlows);
        Assert.Empty(summary.UnexpectedFlows);
    }

    [Fact]
    public void SendWithoutHandle_AppearsInMissing()
    {
        var acct = new FlowAccounting();
        var flowId = Guid.NewGuid();

        acct.RecordSend(flowId, expectedHandlerInvocations: 1);

        var summary = acct.Reconcile();
        Assert.Single(summary.MissingFlows, flowId);
    }

    [Fact]
    public void HandleWithoutSend_AppearsInUnexpected()
    {
        var acct = new FlowAccounting();
        var flowId = Guid.NewGuid();

        acct.RecordHandled(flowId);

        var summary = acct.Reconcile();
        Assert.Single(summary.UnexpectedFlows, flowId);
    }

    [Fact]
    public void FanOut_RequiresAllHandlerInvocations()
    {
        var acct = new FlowAccounting();
        var flowId = Guid.NewGuid();

        acct.RecordSend(flowId, expectedHandlerInvocations: 2);
        acct.RecordHandled(flowId);

        var summary = acct.Reconcile();
        Assert.Single(summary.MissingFlows, flowId);

        acct.RecordHandled(flowId);
        summary = acct.Reconcile();
        Assert.Empty(summary.MissingFlows);
    }
}
