using System.Buffers;
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

    [Fact]
    public void ReadSequence_Throws_When_NotComplete()
    {
        var stream = new MessageBusReadStream("seq");
        Assert.Throws<InvalidOperationException>(() => stream.ReadSequence());
    }

    [Fact]
    public void ReadSequence_SinglePacket_ReturnsAllBytes()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(0);
        stream.Write(new byte[] { 1, 2, 3 }, 0);
        var seq = stream.ReadSequence();
        Assert.Equal(3, seq.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, seq.ToArray());
    }

    [Fact]
    public void ReadSequence_MultiplePackets_LinksInOrder()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write(new byte[] { 1, 2 }, 0);
        stream.Write(new byte[] { 3 }, 1);
        stream.Write(new byte[] { 4, 5 }, 2);
        var seq = stream.ReadSequence();
        Assert.Equal(5, seq.Length);
        Assert.False(seq.IsSingleSegment);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, seq.ToArray());
    }

    [Fact]
    public void ReadSequence_OutOfOrderWrites_ReassemblesInOrder()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write(new byte[] { 4, 5 }, 2);
        stream.Write(new byte[] { 1, 2 }, 0);
        stream.Write(new byte[] { 3 }, 1);
        var seq = stream.ReadSequence();
        Assert.False(seq.IsSingleSegment);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, seq.ToArray());
    }

    [Fact]
    public void IsComplete_ReturnsFalse_WhenPacketSetIsNonContiguous()
    {
        // Write packets 0, 1, and 999 with LastPacketNumber=2. _receivedCount == 3 and
        // LastPacketNumber+1 == 3 — the current IsComplete returns true and Read/ReadSequence
        // silently returns truncated bytes. Fix: Write must reject packetNumber > LastPacketNumber
        // (when LastPacketNumber is already set) so this state is unreachable.
        // After the fix, Write(data, 999) throws ArgumentOutOfRangeException, so IsComplete
        // cannot return true for a non-contiguous set.

        var stream = new MessageBusReadStream("seq");
        stream.Write(new byte[] { 1 }, 0);
        stream.Write(new byte[] { 2 }, 1);
        stream.SetLastPacketNumber(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[] { 9 }, 999));
        Assert.False(stream.IsComplete());
    }
}
