using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public class TryEnrichTests : IDisposable
{
    private readonly Action<Activity, Message>? _originalMessageEnricher;
    private readonly Action<Activity, byte[]>? _originalBytesEnricher;

    public TryEnrichTests()
    {
        _originalMessageEnricher = ServiceConnectActivitySource.Options.EnrichWithMessage;
        _originalBytesEnricher = ServiceConnectActivitySource.Options.EnrichWithMessageBytes;
    }

    public void Dispose()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage = _originalMessageEnricher;
        ServiceConnectActivitySource.Options.EnrichWithMessageBytes = _originalBytesEnricher;
    }

    [Fact]
    public void TryEnrich_Message_OperationCanceledException_PropagatesNotSwallowed()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage = (_, _) =>
            throw new OperationCanceledException("cancelled");

        using var activity = new Activity("t").Start();

        Assert.Throws<OperationCanceledException>(
            () => ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, new TestMsg()));
    }

    [Fact]
    public void TryEnrich_Message_Exception_TagIsTypeName_NotMessage()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage = (_, _) =>
            throw new InvalidOperationException("super-secret-PII-string");

        using var activity = new Activity("t").Start();
        ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, new TestMsg());

        var tag = activity.GetTagItem("enrichment.exception") as string;
        Assert.NotNull(tag);
        Assert.Contains("InvalidOperationException", tag);
        Assert.DoesNotContain("super-secret-PII-string", tag);
    }

    [Fact]
    public void TryEnrich_Bytes_OperationCanceledException_PropagatesNotSwallowed()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessageBytes = (_, _) =>
            throw new OperationCanceledException("cancelled");

        using var activity = new Activity("t").Start();

        Assert.Throws<OperationCanceledException>(
            () => ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, [1]));
    }

    [Fact]
    public void TryEnrich_Bytes_Exception_TagIsTypeName_NotMessage()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessageBytes = (_, _) =>
            throw new InvalidOperationException("PII-from-payload");

        using var activity = new Activity("t").Start();
        ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, [1]);

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
