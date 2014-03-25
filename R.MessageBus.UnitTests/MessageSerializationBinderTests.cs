using System;
using System.Collections.Generic;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class TestKnownType : Message
    {
        public TestKnownType(Guid correlationId)
            : base(correlationId)
        {
        }
    }

    public class MessageSerializationBinderTests
    {
        [Fact]
        public void ShouldBindToKnownTypes()
        {
            // Arrange
            IList<Type> messageTypes = new List<Type>();
            messageTypes.Add(typeof(TestKnownType));
            var serializationBinder = new MessageSerializationBinder(messageTypes);

            // Act
            var result = serializationBinder.BindToType("R.MessageBus.UnitTests", "R.MessageBus.UnitTests.TestKnownType");

            // Assert
            Assert.Equal(typeof(TestKnownType), result);
        }

        [Fact]
        public void ShouldBindToLoadedTypes()
        {
            // Arrange
            var serializationBinder = new MessageSerializationBinder();

            // Act
            var result = serializationBinder.BindToType("R.MessageBus.UnitTests", "R.MessageBus.UnitTests.TestKnownType");

            // Assert
            Assert.Equal(typeof(TestKnownType), result);
        }
    }
}
