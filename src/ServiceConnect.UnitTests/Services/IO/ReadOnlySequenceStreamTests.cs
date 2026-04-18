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
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        Assert.Equal(6, total);
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
    public void Length_ReturnsSequenceLength()
    {
        var seq = BuildMultiSegment([1, 2], [3, 4, 5]);
        using var stream = new ReadOnlySequenceStream(seq);
        Assert.Equal(5, stream.Length);
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
