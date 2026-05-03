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
}
