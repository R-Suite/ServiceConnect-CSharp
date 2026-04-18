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
    public void Decode_UnexpectedIntType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => HeaderDecoder.Decode(42));
        Assert.Contains("System.Int32", ex.Message);
    }

    [Fact]
    public void Decode_UnexpectedGuidType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => HeaderDecoder.Decode(Guid.NewGuid()));
        Assert.Contains("System.Guid", ex.Message);
    }
}
