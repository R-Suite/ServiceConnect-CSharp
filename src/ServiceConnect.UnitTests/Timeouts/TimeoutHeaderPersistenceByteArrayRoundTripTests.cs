using System.Text;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Timeouts;

/// <summary>
/// Locks in the <see cref="TimeoutHeaderPersistence.BuildOutgoingHeaders"/> byte[]→"base64:"
/// arm for non-AMQP byte[] sources. Inbound RabbitMQ byte[] headers are eager-decoded to
/// strings at the consume boundary (see RabbitMqConsumerHost.CopyInboundHeaders), so the
/// byte[] arm is reachable only via direct programmatic API or persistence-layer
/// deserialisation that produces byte[] (e.g. MongoDB BSON binary). The arm exists to keep
/// those values round-trippable to receivers without UTF-8 corruption.
/// </summary>
public class TimeoutHeaderPersistenceByteArrayRoundTripTests
{
    [Fact]
    public void BuildOutgoingHeaders_PrefixesByteArrayValueWithBase64Marker()
    {
        var originalBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var stored = new Dictionary<string, object>
        {
            ["X-Bytes"] = originalBytes,
        };

        var outgoing = TimeoutHeaderPersistence.BuildOutgoingHeaders(stored);

        Assert.True(outgoing.ContainsKey("X-Bytes"));
        Assert.StartsWith(TimeoutHeaderPersistence.BinaryHeaderPrefix, outgoing["X-Bytes"], StringComparison.Ordinal);

        // Strip the prefix and base64-decode → original bytes.
        var encoded = outgoing["X-Bytes"][TimeoutHeaderPersistence.BinaryHeaderPrefix.Length..];
        var decoded = Convert.FromBase64String(encoded);
        Assert.Equal(originalBytes, decoded);
    }

    [Fact]
    public void BuildOutgoingHeaders_StringValuePassesThroughVerbatim()
    {
        // Inbound AMQP byte[] is eager-decoded to string before CaptureForStorage runs, so a
        // user-supplied base64 string in headers comes through CaptureForStorage as a string,
        // not byte[]. BuildOutgoingHeaders must NOT re-prefix it with "base64:" — the string
        // arm of the value-conversion switch returns the string verbatim.
        var stored = new Dictionary<string, object>
        {
            ["X-Base64-String"] = Convert.ToBase64String(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
        };

        var outgoing = TimeoutHeaderPersistence.BuildOutgoingHeaders(stored);

        Assert.Equal("3q2+7w==", outgoing["X-Base64-String"]);
        Assert.DoesNotContain(TimeoutHeaderPersistence.BinaryHeaderPrefix, outgoing["X-Base64-String"]);
    }
}
