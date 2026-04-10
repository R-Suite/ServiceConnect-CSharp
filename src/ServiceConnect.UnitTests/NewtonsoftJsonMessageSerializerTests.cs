using System;
using System.Text;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class NewtonsoftJsonMessageSerializerTests
    {
        private readonly NewtonsoftJsonMessageSerializer _serializer;

        public NewtonsoftJsonMessageSerializerTests()
        {
            _serializer = new NewtonsoftJsonMessageSerializer();
        }

        [Fact]
        public void Serialize_ReturnsNonEmptyBytes()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Alice" };

            var bytes = _serializer.Serialize(message);

            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);
        }

        [Fact]
        public void Serialize_ThrowsSerializationException_WhenMessageIsNull()
        {
            Assert.Throws<SerializationException>(() => _serializer.Serialize<FakeMessage1>(null!));
        }

        [Fact]
        public void Deserialize_Generic_RoundTripsMessage()
        {
            var original = new FakeMessage1(Guid.NewGuid()) { Username = "Bob" };
            var bytes = _serializer.Serialize(original);

            var result = _serializer.Deserialize<FakeMessage1>(bytes);

            Assert.NotNull(result);
            Assert.Equal(original.Username, result.Username);
            Assert.Equal(original.CorrelationId, result.CorrelationId);
        }

        [Fact]
        public void Deserialize_ByType_RoundTripsMessage()
        {
            var original = new FakeMessage1(Guid.NewGuid()) { Username = "Carol" };
            var bytes = _serializer.Serialize(original);

            var result = _serializer.Deserialize(bytes, typeof(FakeMessage1));

            Assert.NotNull(result);
            var typed = Assert.IsType<FakeMessage1>(result);
            Assert.Equal(original.Username, typed.Username);
        }

        [Fact]
        public void Deserialize_ThrowsSerializationException_OnInvalidJson()
        {
            var invalidBytes = Encoding.UTF8.GetBytes("{ this is not valid json !!!");

            Assert.Throws<SerializationException>(() =>
                _serializer.Deserialize(invalidBytes, typeof(FakeMessage1)));
        }

        [Fact]
        public void Deserialize_ThrowsSerializationException_WhenDeserializationReturnsNull()
        {
            var nullBytes = Encoding.UTF8.GetBytes("null");

            Assert.Throws<SerializationException>(() =>
                _serializer.Deserialize(nullBytes, typeof(FakeMessage1)));
        }
    }
}
