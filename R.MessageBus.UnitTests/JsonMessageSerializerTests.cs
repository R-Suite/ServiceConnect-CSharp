using System;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class TestMessage : Message
    {
        public TestMessage(Guid correlationId) : base(correlationId)
        {}

        public string Name { get; set; }
    }

    public class JsonMessageSerializerTests
    {
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        [Fact]
        public void ShouldSerializeMessage()
        {
            // Arrange
            var testMessage = new TestMessage(new Guid("b203e3e4-0c93-4657-a992-6fc75a074a8c")) { Name = "TestName" };

            // Act
            var result = _serializer.Serialize(testMessage);

            // Assert
            const string expectedResult = "{\r\n  \"$type\": \"R.MessageBus.UnitTests.TestMessage, R.MessageBus.UnitTests\",\r\n  \"Name\": \"TestName\",\r\n  \"CorrelationId\": \"b203e3e4-0c93-4657-a992-6fc75a074a8c\"\r\n}";
            Assert.Equal(expectedResult, result);
        }


        [Fact]
        public void ShouldDeserializeMessage()
        {
            // Arrange
            const string message = "{\r\n  \"$type\": \"R.MessageBus.UnitTests.TestMessage, R.MessageBus.UnitTests\",\r\n  \"Name\": \"TestName\",\r\n  \"CorrelationId\": \"b203e3e4-0c93-4657-a992-6fc75a074a8c\"\r\n}";

            // Act
            var result = _serializer.Deserialize(message);

            // Assert
            var expectedResult = new TestMessage(new Guid("b203e3e4-0c93-4657-a992-6fc75a074a8c")) { Name = "TestName" };
            Assert.Equal(typeof(TestMessage), result.GetType());
            Assert.Equal(expectedResult.Name, ((TestMessage)result).Name);
            Assert.Equal(expectedResult.CorrelationId, ((TestMessage)result).CorrelationId);
        }
    }
}
