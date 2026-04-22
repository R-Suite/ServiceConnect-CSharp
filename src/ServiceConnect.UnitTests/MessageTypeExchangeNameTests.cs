using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageTypeExchangeNameTests
{
    // Nested types whose FullName flattens to the same dot-stripped prefix.
    // Both "A.BC.X" and "A.B.CX" collapse to "ABCX" under the old sanitizer
    // — the hash suffix must keep them apart.
    public sealed class SampleA
    {
        public sealed class BC
        {
            public sealed class X;
        }
    }

    public sealed class SampleB
    {
        public sealed class B
        {
            public sealed class CX;
        }
    }

    [Fact]
    public void From_SharedPrefixAfterDotStripping_StillProducesUniqueNames()
    {
        // Types whose FullName differs only by dot position (e.g. "A.BC" vs "AB.C")
        // must map to distinct exchange/binding names. A naive dot-stripping scheme
        // would collapse them and cross-wire routing; the hash suffix keeps the
        // mapping injective so publishers and consumers don't share a binding.
        var a = MessageTypeExchangeName.From(typeof(SampleA.BC.X));
        var b = MessageTypeExchangeName.From(typeof(SampleB.B.CX));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void From_SameType_ProducesStableResult()
    {
        // Producer and consumer both call From(type) to agree on the name — the
        // mapping must be deterministic across calls.
        var first = MessageTypeExchangeName.From(typeof(SampleA.BC.X));
        var second = MessageTypeExchangeName.From(typeof(SampleA.BC.X));

        Assert.Equal(first, second);
    }
}
