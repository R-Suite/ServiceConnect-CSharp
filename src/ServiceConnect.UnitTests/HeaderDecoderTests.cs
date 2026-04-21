using System.Text;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

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
    public void Decode_DictionaryHeader_FallsBackToString_DoesNotThrow()
    {
        IDictionary<string, object> dict = new Dictionary<string, object> { ["k"] = "v" };
        // ToString() on a dictionary returns its type name; the exact content is
        // unspecified — the invariant is that it does not throw.
        var decoded = HeaderDecoder.Decode(dict);
        Assert.NotNull(decoded);
    }

    [Fact]
    public void Decode_ArbitraryObject_FallsBackToString_DoesNotThrow()
    {
        var obj = new object();
        var decoded = HeaderDecoder.Decode(obj);
        Assert.NotNull(decoded);
    }
}
