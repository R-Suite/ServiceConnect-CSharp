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

    [Fact]
    public void TryRemoveCompleted_RemovesFullyHandledFlows_LeavesMissingIntact()
    {
        var acct = new FlowAccounting();
        var completed = Guid.NewGuid();
        var missing = Guid.NewGuid();

        acct.RecordSend(completed, expectedHandlerInvocations: 1);
        acct.RecordHandled(completed);
        acct.RecordSend(missing, expectedHandlerInvocations: 2);
        acct.RecordHandled(missing);     // only 1 of 2

        acct.TryRemoveCompleted();

        var summary = acct.Reconcile();
        Assert.Equal(2, summary.SentCount);          // 'missing' still tracked (expected=2)
        Assert.Equal(1, summary.HandledCount);       // only 'missing's 1 handle still tracked
        Assert.Single(summary.MissingFlows, missing);
        Assert.Empty(summary.UnexpectedFlows);
    }
}
