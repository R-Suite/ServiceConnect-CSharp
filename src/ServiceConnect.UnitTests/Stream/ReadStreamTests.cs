//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using ServiceConnect.Core;
using Xunit;

namespace ServiceConnect.UnitTests.Stream
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