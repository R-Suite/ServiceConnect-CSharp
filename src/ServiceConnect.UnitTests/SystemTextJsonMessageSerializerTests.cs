using System;
using System.Buffers;
using System.Text;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class SystemTextJsonMessageSerializerTests
{
    private readonly SystemTextJsonMessageSerializer _serializer;

    public SystemTextJsonMessageSerializerTests()
    {
        _serializer = new SystemTextJsonMessageSerializer();
    }

    [Fact]
    public void Serialize_WritesNonEmptyBytes()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Alice" };

        var bw = new ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bw);

        Assert.True(bw.WrittenCount > 0);
    }

    [Fact]
    public void Serialize_ThrowsSerializationException_WhenMessageIsNull()
    {
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<SerializationException>(() => _serializer.Serialize<FakeMessage1>(null!, bw));
    }

    [Fact]
    public void Serialize_ThrowsArgumentNull_WhenOutputIsNull()
    {
        var message = new FakeMessage1(Guid.NewGuid());
        Assert.Throws<ArgumentNullException>(() => _serializer.Serialize<FakeMessage1>(message, null!));
    }

    [Fact]
    public void Deserialize_Generic_RoundTripsMessage()
    {
        var original = new FakeMessage1(Guid.NewGuid()) { Username = "Bob" };
        var bw = new ArrayBufferWriter<byte>();
        _serializer.Serialize(original, bw);

        var result = _serializer.Deserialize<FakeMessage1>(bw.WrittenMemory);

        Assert.NotNull(result);
        Assert.Equal(original.Username, result.Username);
        Assert.Equal(original.CorrelationId, result.CorrelationId);
    }

    [Fact]
    public void Deserialize_ByType_RoundTripsMessage()
    {
        var original = new FakeMessage1(Guid.NewGuid()) { Username = "Carol" };
        var bw = new ArrayBufferWriter<byte>();
        _serializer.Serialize(original, bw);

        var result = _serializer.Deserialize(bw.WrittenMemory, typeof(FakeMessage1));

        Assert.NotNull(result);
        var typed = Assert.IsType<FakeMessage1>(result);
        Assert.Equal(original.Username, typed.Username);
    }

    [Fact]
    public void Deserialize_ThrowsSerializationException_OnInvalidJson()
    {
        var invalidBytes = Encoding.UTF8.GetBytes("{ this is not valid json !!!");

        Assert.Throws<SerializationException>(() =>
            _serializer.Deserialize((ReadOnlyMemory<byte>)invalidBytes.AsMemory(), typeof(FakeMessage1)));
    }

    [Fact]
    public void Deserialize_ThrowsSerializationException_WhenDeserializationReturnsNull()
    {
        var nullBytes = Encoding.UTF8.GetBytes("null");

        Assert.Throws<SerializationException>(() =>
            _serializer.Deserialize((ReadOnlyMemory<byte>)nullBytes.AsMemory(), typeof(FakeMessage1)));
    }

    [Fact]
    public void Deserialize_ReadOnlySequence_AcrossSegmentBoundary_RoundTripsMessage()
    {
        // Construct a ReadOnlySequence<byte> whose JSON token straddles a segment boundary.
        // The STJ override of Deserialize(in ReadOnlySequence<byte>, Type) must read across
        // segments via Utf8JsonReader without flattening — this test guards the zero-copy
        // streaming path against future refactors that might silently revert to ToArray().
        var original = new FakeMessage1(Guid.NewGuid()) { Username = "across-segment" };
        var bw = new ArrayBufferWriter<byte>();
        _serializer.Serialize(original, bw);
        var fullBytes = bw.WrittenSpan.ToArray();

        // Split the payload mid-token (16 bytes lands inside a property name or value for a
        // typical FakeMessage1 serialisation, exercising the multi-segment Utf8JsonReader path).
        var split = fullBytes.Length / 2;
        var firstSegment = new ArraySegment<byte>(fullBytes, 0, split);
        var secondSegment = new ArraySegment<byte>(fullBytes, split, fullBytes.Length - split);

        var first = new ByteSegment(firstSegment);
        var second = first.Append(secondSegment);
        var sequence = new ReadOnlySequence<byte>(first, 0, second, secondSegment.Count);

        Assert.False(sequence.IsSingleSegment, "Test setup must produce a multi-segment sequence.");

        var result = _serializer.Deserialize(in sequence, typeof(FakeMessage1));

        var typed = Assert.IsType<FakeMessage1>(result);
        Assert.Equal(original.Username, typed.Username);
        Assert.Equal(original.CorrelationId, typed.CorrelationId);
    }

    private sealed class ByteSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public ByteSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
