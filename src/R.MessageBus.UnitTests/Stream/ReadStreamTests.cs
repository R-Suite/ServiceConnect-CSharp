using System;
using R.MessageBus.Core;
using Xunit;

namespace R.MessageBus.UnitTests.Stream
{
    public class ReadStreamTests
    {
        [Fact]
        public void ShouldReadPacketAfterWritingToStream()
        {
            // Arrange
            var stream = new MessageBusReadStream();
            var bytes = new byte[10];
            stream.Write(bytes, 1);

            // Act
            var result = stream.Read();

            // Assert
            Assert.Equal(bytes, result);
        }

        [Fact]
        public void ShouldNotReadFromStreamIfPacketNumberHasntYetBeenWritten()
        {
            // Arrange
            var stream = new MessageBusReadStream();
            var bytes = new byte[10];
            stream.Write(bytes, 2);

            // Act
            var result = stream.Read();

            // Assert
            Assert.Empty(result);
            Assert.NotEqual(bytes, result);
        }

        [Fact]
        public void IsCompleteShouldReturnTrueIfAllPacketsHaveBeenRead()
        {
            // Arrange
            var stream = new MessageBusReadStream {LastPacketNumber = 3, CompleteEventHandler = CompleteEventHandler};
            var bytes = new byte[10];
            stream.Write(bytes, 1);
            stream.Write(bytes, 2);
            stream.Read();
            stream.Read();

            // Act
            var result = stream.IsComplete();

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void IsCompleteShouldReturnFalseIfAllPacketsHaveBeenRead()
        {
            // Arrange
            var stream = new MessageBusReadStream { LastPacketNumber = 3, CompleteEventHandler = CompleteEventHandler };
            var bytes = new byte[10];
            stream.Write(bytes, 1);
            stream.Write(bytes, 2);

            // Act
            var result = stream.IsComplete();

            // Assert
            Assert.False(result);
        }

        private bool _complete;

        [Fact]
        public void IsCompleteShouldExecuteCompleteEventHandlerIfAllPacketsHaveBeenRead()
        {
            // Arrange
            var stream = new MessageBusReadStream { LastPacketNumber = 3, CompleteEventHandler = CompleteEventHandler };
            var bytes = new byte[10];
            stream.Write(bytes, 1);
            stream.Write(bytes, 2);
            stream.Read();
            stream.Read();

            // Act
            stream.IsComplete();

            // Assert
            Assert.True(_complete);
        }

        [Fact]
        public void IsCompleteShouldNotExecuteCompleteEventHandlerIfAllPacketsHaveBeenRead()
        {
            // Arrange
            var stream = new MessageBusReadStream { LastPacketNumber = 3, CompleteEventHandler = CompleteEventHandler };
            var bytes = new byte[10];
            stream.Write(bytes, 1);
            stream.Write(bytes, 2);
            stream.Read();

            // Act
            stream.IsComplete();

            // Assert
            Assert.False(_complete);
        }

        private void CompleteEventHandler(string sequenceId)
        {
            _complete = true;
        }
    }
}