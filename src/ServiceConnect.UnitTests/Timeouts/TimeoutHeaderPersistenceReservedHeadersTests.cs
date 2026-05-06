using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Timeouts;

public class TimeoutHeaderPersistenceReservedHeadersTests
{
    [Theory]
    [InlineData(HeaderKeys.RetryCount)]
    [InlineData(HeaderKeys.CorrelationId)]
    [InlineData(HeaderKeys.Priority)]
    public void CaptureForStorage_DropsReservedHeader(string reservedKey)
    {
        var headers = new Dictionary<string, object>
        {
            [reservedKey] = "should-not-be-captured",
            ["UserHeader"] = "should-be-captured",
        };

        var captured = TimeoutHeaderPersistence.CaptureForStorage(headers);

        Assert.False(captured.ContainsKey(reservedKey),
            $"{reservedKey} must not be persisted into stored timeout headers — re-emitting it as a fresh outbound message corrupts the per-delivery semantics.");
        Assert.True(captured.ContainsKey("UserHeader"));
    }

    [Theory]
    [InlineData(HeaderKeys.RetryCount)]
    [InlineData(HeaderKeys.CorrelationId)]
    [InlineData(HeaderKeys.Priority)]
    public void BuildOutgoingHeaders_DropsReservedHeader(string reservedKey)
    {
        var stored = new Dictionary<string, object>
        {
            [reservedKey] = "stale-value",
            ["UserHeader"] = "carry-through",
        };

        var outgoing = TimeoutHeaderPersistence.BuildOutgoingHeaders(stored);

        Assert.False(outgoing.ContainsKey(reservedKey));
        Assert.True(outgoing.ContainsKey("UserHeader"));
    }
}
