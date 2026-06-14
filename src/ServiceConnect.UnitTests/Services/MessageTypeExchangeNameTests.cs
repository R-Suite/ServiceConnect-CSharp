using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageTypeExchangeNameTests
{
    public sealed class SampleMessage;

    [Fact]
    public void From_ReturnsFullNameWithDotsRemoved()
    {
        // Master convention: the exchange/binding name is Type.FullName with the namespace
        // dots removed (no hash suffix), so the C# and Node runtimes share the same exchange.
        var type = typeof(SampleMessage);
        var expected = type.FullName!.Replace(".", string.Empty);

        Assert.Equal(expected, MessageTypeExchangeName.From(type));
    }

    [Fact]
    public void From_SameType_ProducesStableResult()
    {
        // Producer and consumer both call From(type) to agree on the name — the
        // mapping must be deterministic across calls.
        var first = MessageTypeExchangeName.From(typeof(SampleMessage));
        var second = MessageTypeExchangeName.From(typeof(SampleMessage));

        Assert.Equal(first, second);
    }

    [Fact]
    public void From_HasNoHashSuffix_MatchesMaster()
    {
        // Regression guard: the name must be exactly the flattened FullName with no
        // underscore-hash suffix, so it stays byte-identical to master and Node on the wire.
        var actual = MessageTypeExchangeName.From(typeof(SampleMessage));

        Assert.DoesNotContain('_', actual);
        Assert.Equal(typeof(SampleMessage).FullName!.Replace(".", string.Empty), actual);
    }
}
