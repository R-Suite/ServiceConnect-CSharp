using System.Text;
using ServiceConnect.Services.IO;
using Xunit;

namespace ServiceConnect.UnitTests.Services.IO;

public class ReadOnlyMemoryStreamTests
{
    [Fact]
    public void Read_ReturnsAllBytes_InOrder()
    {
        var source = Encoding.UTF8.GetBytes("hello world");
        using var stream = new ReadOnlyMemoryStream(source);

        var buffer = new byte[source.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(source.Length, read);
        Assert.Equal(source, buffer);
    }

    [Fact]
    public void Read_AcrossMultipleCalls_ConcatenatesToFullPayload()
    {
        var source = Encoding.UTF8.GetBytes("abcdefghij");
        using var stream = new ReadOnlyMemoryStream(source);

        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' }, buffer);

        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { (byte)'e', (byte)'f', (byte)'g', (byte)'h' }, buffer);

        var tail = new byte[4];
        Assert.Equal(2, stream.Read(tail, 0, 4));
        Assert.Equal((byte)'i', tail[0]);
        Assert.Equal((byte)'j', tail[1]);
    }

    [Fact]
    public void Read_AfterEnd_ReturnsZero()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[] { 1, 2 });
        var buffer = new byte[4];
        Assert.Equal(2, stream.Read(buffer, 0, 4));
        Assert.Equal(0, stream.Read(buffer, 0, 4));
    }

    [Fact]
    public void CanSeek_IsFalse()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[0]);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Length_MatchesInputLength()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[17]);
        Assert.Equal(17, stream.Length);
    }
}
