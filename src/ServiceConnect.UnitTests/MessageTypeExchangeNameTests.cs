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
        // M5 regression: the old helper was FullName.Replace(".", "") which meant
        // two types whose names differed only by dot position collapsed onto the
        // same exchange/binding name, cross-wiring routing. Hash suffix restores
        // uniqueness.
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
