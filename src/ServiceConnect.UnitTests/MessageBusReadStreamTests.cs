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
        stream.Write([1, 2], 0);
        stream.Write([5, 6], 2);
        stream.Write([3, 4], 1);

        Assert.True(stream.IsComplete());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, stream.Read());
    }

    [Fact]
    public void IsComplete_MissingPacket_ReturnsFalse()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write([1], 0);
        stream.Write([3], 2);

        Assert.False(stream.IsComplete());
    }

    [Fact]
    public void IsComplete_NoLastPacketNumber_ReturnsFalse()
    {
        var stream = new MessageBusReadStream("seq");
        stream.Write([1], 0);

        Assert.False(stream.IsComplete());
    }

    [Fact]
    public void Read_WhenNotComplete_ThrowsInvalidOperationException()
    {
        var stream = new MessageBusReadStream("seq");
        stream.Write([1], 0);

        Assert.Throws<InvalidOperationException>(stream.Read);
    }

    [Fact]
    public void Write_DuplicatePacketNumber_DoesNotThrow()
    {
        // Broker re-delivery is a routine occurrence — the second arrival of the same
        // packet number is treated as an idempotent ack rather than a stream-corruption
        // signal that would nack-with-requeue and produce a poison loop.
        var stream = new MessageBusReadStream("seq");
        stream.Write([1, 2], 0);

        var ex = Record.Exception(() => stream.Write("\t\t"u8.ToArray(), 0));

        Assert.Null(ex);
    }

    [Fact]
    public void Write_DuplicatePacketNumber_FirstPayloadWins_AndStreamIsComplete()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(0);
        stream.Write([1, 2], 0);
        stream.Write("\t\t"u8.ToArray(), 0); // ignored

        Assert.True(stream.IsComplete());
        Assert.Equal(new byte[] { 1, 2 }, stream.Read());
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
        stream.Write([1, 2, 3], 0);
        var seq = stream.ReadSequence();
        Assert.Equal(3, seq.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, seq.ToArray());
    }

    [Fact]
    public void ReadSequence_MultiplePackets_LinksInOrder()
    {
        var stream = new MessageBusReadStream("seq");
        stream.SetLastPacketNumber(2);
        stream.Write([1, 2], 0);
        stream.Write([3], 1);
        stream.Write([4, 5], 2);
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
        stream.Write([4, 5], 2);
        stream.Write([1, 2], 0);
        stream.Write([3], 1);
        var seq = stream.ReadSequence();
        Assert.False(seq.IsSingleSegment);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, seq.ToArray());
    }

    [Fact]
    public void SetLastPacketNumber_AfterValidPacket_HappyPath()
    {
        // Packet 0 arrives, then the close-packet declares LastPacketNumber=0 — consistent.
        var stream = new MessageBusReadStream("seq");
        stream.Write([1], 0);

        var ex = Record.Exception(() => stream.SetLastPacketNumber(0));

        Assert.Null(ex);
        Assert.True(stream.IsComplete());
    }

    [Fact]
    public void SetLastPacketNumber_WhenAlreadyReceivedPacketExceedsIt_Throws()
    {
        // Packet 5 arrives before the close-packet declares LastPacketNumber=2 — inconsistent.
        var stream = new MessageBusReadStream("seq");
        stream.Write("\t"u8.ToArray(), 5);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.SetLastPacketNumber(2));

        Assert.Contains("5", ex.Message);
        Assert.Contains("2", ex.Message);
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
        stream.Write([1], 0);
        stream.Write([2], 1);
        stream.SetLastPacketNumber(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write("\t"u8.ToArray(), 999));
        Assert.False(stream.IsComplete());
    }
}
