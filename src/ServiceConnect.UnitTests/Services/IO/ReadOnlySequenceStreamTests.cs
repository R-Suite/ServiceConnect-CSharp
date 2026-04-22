using System.Buffers;
using ServiceConnect.Services.IO;
using Xunit;

namespace ServiceConnect.UnitTests.Services.IO;

public class ReadOnlySequenceStreamTests
{
    [Fact]
    public void Read_SingleSegment_ReturnsAllBytes()
    {
        var seq = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5 });
        using var stream = new ReadOnlySequenceStream(seq);
        var buffer = new byte[5];
        Assert.Equal(5, stream.Read(buffer, 0, 5));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer);
    }

    [Fact]
    public void Read_MultiSegment_ReadsAcrossBoundary()
    {
        var seq = BuildMultiSegment([1, 2, 3], [4, 5, 6]);
        using var stream = new ReadOnlySequenceStream(seq);
        var buffer = new byte[6];
        Assert.Equal(6, stream.Read(buffer, 0, 6));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, buffer);
    }

    [Fact]
    public void Read_AfterEnd_ReturnsZero()
    {
        var seq = new ReadOnlySequence<byte>(new byte[] { 1, 2 });
        using var stream = new ReadOnlySequenceStream(seq);
        var buffer = new byte[2];
        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(0, stream.Read(buffer, 0, 2));
    }

    [Fact]
    public void Length_Throws_BecauseCanSeekIsFalse()
    {
        // Per the Stream contract, a non-seekable stream must throw on Length.
        // Returning the sequence length here would mislead callers into performing
        // random access on a forward-only stream.
        var seq = BuildMultiSegment([1, 2], [3, 4, 5]);
        using var stream = new ReadOnlySequenceStream(seq);
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    [Fact]
    public void Read_EmptySequence_ReturnsZero()
    {
        using var stream = new ReadOnlySequenceStream(new ReadOnlySequence<byte>());
        var buffer = new byte[4];
        Assert.Equal(0, stream.Read(buffer, 0, 4));
    }

    private static ReadOnlySequence<byte> BuildMultiSegment(byte[] first, byte[] second)
    {
        var firstSeg = new TestSegment(first);
        var secondSeg = firstSeg.Append(second);
        return new ReadOnlySequence<byte>(firstSeg, 0, secondSeg, second.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new TestSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
