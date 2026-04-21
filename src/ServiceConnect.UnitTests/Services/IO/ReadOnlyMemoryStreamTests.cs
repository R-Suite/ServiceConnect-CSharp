using ServiceConnect.Services.IO;
using Xunit;

namespace ServiceConnect.UnitTests.Services.IO;

public class ReadOnlyMemoryStreamTests
{
    [Fact]
    public void Read_ReturnsBytesInOrder()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new ReadOnlyMemoryStream(data);
        var buffer = new byte[5];
        var read = stream.Read(buffer, 0, 5);
        Assert.Equal(5, read);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void Read_MultipleCallsReturnsRemainingBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new ReadOnlyMemoryStream(data);
        var buffer = new byte[3];
        var first = stream.Read(buffer, 0, 3);
        var second = stream.Read(buffer, 0, 3);
        Assert.Equal(3, first);
        Assert.Equal(2, second);
        Assert.Equal(new byte[] { 4, 5, 3 }, buffer);
    }

    [Fact]
    public void Read_AfterEndReturnsZero()
    {
        var data = new byte[] { 1, 2 };
        using var stream = new ReadOnlyMemoryStream(data);
        var buffer = new byte[2];
        Assert.Equal(2, stream.Read(buffer, 0, 2));
        var read = stream.Read(buffer, 0, 2);
        Assert.Equal(0, read);
    }

    [Fact]
    public void CanSeek_IsFalse()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[] { 1 });
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void Length_Throws_BecauseCanSeekIsFalse()
    {
        // L3 regression: Length used to return the buffer length even though CanSeek
        // was false, violating the Stream contract.
        using var stream = new ReadOnlyMemoryStream(new byte[] { 1, 2, 3 });
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }
}
