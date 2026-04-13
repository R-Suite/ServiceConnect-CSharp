using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageBusReadStreamTests
{
    [Fact]
    public void Write_And_Read_ReassemblesPacketsInOrder()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write(new byte[] { 1, 2 }, 0);
        stream.Write(new byte[] { 5, 6 }, 2);
        stream.Write(new byte[] { 3, 4 }, 1);

        Assert.True(stream.IsComplete());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, stream.Read());
    }

    [Fact]
    public void IsComplete_MissingPacket_ReturnsFalse()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write(new byte[] { 1 }, 0);
        stream.Write(new byte[] { 3 }, 2);

        Assert.False(stream.IsComplete());
    }

    [Fact]
    public void IsComplete_NoLastPacketNumber_ReturnsFalse()
    {
        var stream = new MessageBusReadStream("seq");
        stream.Write(new byte[] { 1 }, 0);

        Assert.False(stream.IsComplete());
    }

    [Fact]
    public void Read_WhenNotComplete_ThrowsInvalidOperationException()
    {
        var stream = new MessageBusReadStream("seq");
        stream.Write(new byte[] { 1 }, 0);

        Assert.Throws<InvalidOperationException>(() => stream.Read());
    }

    [Fact]
    public void Write_DuplicatePacketNumber_Throws()
    {
        var stream = new MessageBusReadStream("seq");
        stream.Write(new byte[] { 1, 2 }, 0);
        Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[] { 3, 4 }, 0));
    }
}
