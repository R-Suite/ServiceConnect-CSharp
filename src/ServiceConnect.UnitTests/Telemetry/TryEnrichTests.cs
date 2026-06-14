using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public class TryEnrichTests
{
    [Fact]
    public void TryEnrich_Message_OperationCanceledException_PropagatesNotSwallowed()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new OperationCanceledException("cancelled")
        };

        using var activity = new Activity("t").Start();

        Assert.Throws<OperationCanceledException>(
            () => ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, new TestMsg(), options));
    }

    [Fact]
    public void TryEnrich_Message_Exception_TagIsTypeName_NotMessage()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new InvalidOperationException("super-secret-PII-string")
        };

        using var activity = new Activity("t").Start();
        ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, new TestMsg(), options);

        var tag = activity.GetTagItem("enrichment.exception") as string;
        Assert.NotNull(tag);
        Assert.Contains("InvalidOperationException", tag);
        Assert.DoesNotContain("super-secret-PII-string", tag);
    }

    [Fact]
    public void TryEnrich_Bytes_OperationCanceledException_PropagatesNotSwallowed()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessageBytes = (_, _) => throw new OperationCanceledException("cancelled")
        };

        using var activity = new Activity("t").Start();

        Assert.Throws<OperationCanceledException>(
            () => ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, [1], options));
    }

    [Fact]
    public void TryEnrich_Bytes_Exception_TagIsTypeName_NotMessage()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessageBytes = (_, _) => throw new InvalidOperationException("PII-from-payload")
        };

        using var activity = new Activity("t").Start();
        ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, [1], options);

        var tag = activity.GetTagItem("enrichment.exception") as string;
        Assert.NotNull(tag);
        Assert.Contains("InvalidOperationException", tag);
        Assert.DoesNotContain("PII-from-payload", tag);
    }

    private sealed class TestMsg : Message
    {
        public TestMsg() : base(Guid.NewGuid()) { }
    }
}
