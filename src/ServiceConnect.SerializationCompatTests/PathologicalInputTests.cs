using System.Text;
using System.Text.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.SerializationCompatTests;

/// <summary>
/// STJ-side guard rails for inputs that Newtonsoft tolerated but STJ rejects (or vice versa).
/// These tests document the v8 wire-format edge cases that the release notes call out.
/// </summary>
public class PathologicalInputTests
{
    private static readonly SystemTextJsonMessageSerializer Stj = new();

    [Fact]
    public void DeeplyNested_BeyondMaxDepth_Throws()
    {
        // 50-level nested array exceeds MaxDepth = 32; STJ should throw on parse.
        var nested = new StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            nested.Append('[');
        }

        nested.Append("0");
        for (var i = 0; i < 50; i++)
        {
            nested.Append(']');
        }
        // Wrap as a message for the deserializer's signature.
        var json = $"{{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Field\":{nested}}}";
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsAny<Interfaces.Exceptions.SerializationException>(() =>
            Stj.Deserialize(bytes, typeof(NestedArrayMessage)));
    }

    [Fact]
    public void NaN_Double_Rejected()
    {
        // Explicitly malformed JSON: "NaN" is not a valid JSON literal. Newtonsoft tolerated
        // it; STJ rejects. Documented behaviour change in v8 release notes.
        var json = "{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":NaN}";
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsAny<Interfaces.Exceptions.SerializationException>(() =>
            Stj.Deserialize(bytes, typeof(DoubleMessage)));
    }

    [Fact]
    public void StringEncodedNumber_AcceptedByDefault()
    {
        // Newtonsoft accepts "3" for an int field. STJ does too, with NumberHandling.AllowReadingFromString.
        var json = "{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":\"42\"}";
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = (Int32Message)Stj.Deserialize(bytes, typeof(Int32Message));
        Assert.Equal(42, result.Value);
    }

    private sealed class NestedArrayMessage : Message
    {
        public NestedArrayMessage() : base(Guid.Empty) { }
        public object Field { get; init; } = default!;
    }

    private sealed class DoubleMessage : Message
    {
        public DoubleMessage() : base(Guid.Empty) { }
        public double Value { get; init; }
    }

    private sealed class Int32Message : Message
    {
        public Int32Message() : base(Guid.Empty) { }
        public int Value { get; init; }
    }
}
