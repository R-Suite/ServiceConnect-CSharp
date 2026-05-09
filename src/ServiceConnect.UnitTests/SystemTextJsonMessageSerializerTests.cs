using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using ServiceConnect.Interfaces;
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

    [Fact]
    public void Deserialize_ReadOnlySequence_RejectsPayloadExceedingConfiguredMaxDepth()
    {
        // Build a JSON payload nested 50 levels deep — well above the configured cap of 32.
        // Wrap it in a single-segment ReadOnlySequence so we hit the streaming overload.
        // Pre-fix, the sequence overload silently accepted this because it constructed the
        // Utf8JsonReader with state:default (MaxDepth=64) instead of the configured cap.
        var deepPayload = BuildDeepObject(50);
        var bytes = Encoding.UTF8.GetBytes(deepPayload);
        var sequence = new ReadOnlySequence<byte>(bytes);

        var serializer = new SystemTextJsonMessageSerializer();
        var ex = Assert.Throws<SerializationException>(() => serializer.Deserialize(in sequence, typeof(NestedMessage)));

        // Inner exception is a JsonException for depth-cap violation.
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Deserialize_ReadOnlySequence_AcceptsPayloadWithinConfiguredMaxDepth()
    {
        // Sanity check that depths within the cap still round-trip on the sequence overload.
        var shallowPayload = BuildDeepObject(10);
        var bytes = Encoding.UTF8.GetBytes(shallowPayload);
        var sequence = new ReadOnlySequence<byte>(bytes);

        var serializer = new SystemTextJsonMessageSerializer();
        var result = serializer.Deserialize(in sequence, typeof(NestedMessage));
        Assert.NotNull(result);
    }

    [Fact]
    public void Deserialize_ReadOnlySequence_EnforcesDepthCapAtBoundary()
    {
        // The serializer pins MaxDepth=32 in its ctor for wire-compat with v7's Newtonsoft
        // behaviour. Pre-fix, the sequence overload silently used JsonReaderState's hidden
        // default of 64 — payloads at depth 33–64 were accepted on the streaming hot path
        // but rejected on the byte-span path. Pin the boundary precisely: depth 33 must be
        // rejected by the sequence overload now that it threads _options.MaxDepth through
        // the JsonReaderState.
        var depth33 = BuildDeepObject(33);
        var bytes = Encoding.UTF8.GetBytes(depth33);
        var sequence = new ReadOnlySequence<byte>(bytes);

        var serializer = new SystemTextJsonMessageSerializer();
        var ex = Assert.Throws<SerializationException>(() => serializer.Deserialize(in sequence, typeof(NestedMessage)));
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Deserialize_Span_RejectsPayloadExceedingConfiguredMaxDepth()
    {
        // Parity check — the byte-span overload already enforced the configured cap by
        // threading _options through JsonSerializer.Deserialize. Both overloads must
        // reject the same payload.
        var deepPayload = BuildDeepObject(50);
        var bytes = Encoding.UTF8.GetBytes(deepPayload);

        var serializer = new SystemTextJsonMessageSerializer();
        var ex = Assert.Throws<SerializationException>(() =>
            serializer.Deserialize((ReadOnlyMemory<byte>)bytes.AsMemory(), typeof(NestedMessage)));
        Assert.IsType<JsonException>(ex.InnerException);
    }

    private static string BuildDeepObject(int depth)
    {
        // Produces {"Inner":{"Inner":{...{"Inner":null}}}} — `depth` levels of the "Inner"
        // property chain, terminated by a null value.
        var sb = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            sb.Append("{\"Inner\":");
        }

        sb.Append("null");
        for (var i = 0; i < depth; i++)
        {
            sb.Append('}');
        }

        return sb.ToString();
    }

    private sealed class NestedMessage : Message
    {
        public NestedMessage() : base(Guid.NewGuid()) { }

        public NestedMessage? Inner { get; set; }
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
