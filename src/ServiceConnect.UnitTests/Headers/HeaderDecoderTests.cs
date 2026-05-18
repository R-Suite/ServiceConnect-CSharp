using System.Text;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Headers;

public class HeaderDecoderTests
{
    [Fact]
    public void Decode_NullValue_ReturnsNull()
    {
        Assert.Null(HeaderDecoder.Decode(null));
    }

    [Fact]
    public void Decode_StringValue_ReturnsSameString()
    {
        Assert.Equal("hello", HeaderDecoder.Decode("hello"));
    }

    [Fact]
    public void Decode_ByteArrayValue_ReturnsUtf8DecodedString()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");

        Assert.Equal("payload", HeaderDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_IntegerHeader_FallsBackToString()
    {
        // AMQP integer-typed header values must not throw — a throw here causes
        // the consumer host to nack-with-requeue and infinitely redeliver.
        Assert.Equal("42", HeaderDecoder.Decode(42));
    }

    [Fact]
    public void Decode_LongHeader_FallsBackToString()
    {
        Assert.Equal("9223372036854775807", HeaderDecoder.Decode(long.MaxValue));
    }

    [Fact]
    public void Decode_GuidHeader_FallsBackToString()
    {
        var guid = Guid.Parse("0f3e2c7a-2c39-4f9e-8c2a-22f0d3d6a1aa");
        Assert.Equal(guid.ToString(), HeaderDecoder.Decode(guid));
    }

    [Fact]
    public void Decode_DictionaryHeader_RendersAsJsonShape()
    {
        var nested = new Dictionary<string, object>
        {
            ["a"] = "alpha",
            ["b"] = 42,
        };

        var decoded = HeaderDecoder.Decode(nested);

        Assert.NotNull(decoded);
        // Both keys and both values must appear; ordering is not guaranteed.
        Assert.Contains("a", decoded!);
        Assert.Contains("alpha", decoded);
        Assert.Contains("b", decoded);
        Assert.Contains("42", decoded);
        Assert.DoesNotContain("System.Collections", decoded);
    }

    [Fact]
    public void Decode_ListHeader_RendersAsJsonArrayShape()
    {
        var arr = new List<object> { "x", 1, "y" };

        var decoded = HeaderDecoder.Decode(arr);

        Assert.NotNull(decoded);
        Assert.Contains("x", decoded!);
        Assert.Contains("1", decoded);
        Assert.Contains("y", decoded);
        Assert.DoesNotContain("System.Collections", decoded);
    }

    [Fact]
    public void Decode_ByteArrayInsideList_DecodesAsUtf8()
    {
        var arr = new List<object> { "xyz"u8.ToArray() };
        var decoded = HeaderDecoder.Decode(arr);
        Assert.NotNull(decoded);
        Assert.Contains("xyz", decoded!);
    }

    [Fact]
    public void Decode_RenderingFault_FallsBackToTypeName()
    {
        var thrower = new ThrowOnEnumerate();
        var decoded = HeaderDecoder.Decode(thrower);
        Assert.NotNull(decoded);
        Assert.Contains(nameof(ThrowOnEnumerate), decoded!);
    }

    private sealed class ThrowOnEnumerate : IEnumerable<object>
    {
        public IEnumerator<object> GetEnumerator() => throw new InvalidOperationException("boom");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Decode_ArbitraryObject_FallsBackToString_DoesNotThrow()
    {
        var obj = new object();
        var decoded = HeaderDecoder.Decode(obj);
        Assert.NotNull(decoded);
    }
}
